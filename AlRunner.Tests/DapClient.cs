using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

/// <summary>
/// Spawns al-runner in <c>--dap</c> mode and drives it over the real DAP TCP wire
/// format (AlRunner.Infrastructure.DapTransport — the exact same class the runner
/// itself uses on the server side of this connection), for issue #1642. Unlike
/// CliServer/SharedCliServer, a DAP session is inherently single-shot (one client,
/// one bundle, one run) so there is no shared-process variant of this helper.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly Process _process;
    private readonly TcpClient _tcp;
    private readonly DapTransport _transport;
    private readonly StringBuilder _stderr;
    private readonly StringBuilder _stdout;

    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }
    public string StdOut { get { lock (_stdout) return _stdout.ToString(); } }

    // Client-side half of issue #2070's decisive trace (coordinator request on PR
    // #2076): AlDapSession.Trace (AlRunner/Infrastructure/AlDapSession.cs) already
    // logs ARM/EVAL/FIRE/WAIT with a wall-clock UTC timestamp from inside the spawned
    // al-runner --dap CHILD process. On its own that answers nothing about a client
    // read timeout — it proves the SERVER did something, not whether the CLIENT ever
    // saw it. Logging the client's own "I sent a step command at T" / "I gave up
    // waiting at T+60s" on the SAME wall clock turns the two one-sided traces into one
    // comparable timeline: if the server's FIRE sits at a small elapsed time relative
    // to the client's SEND, but the client's GIVEUP still fires at the full timeout,
    // the server did its job and the client's own socket read simply was not
    // scheduled in time (CPU starvation on an oversubscribed runner) — not a step-
    // logic defect. Gated on the SAME AL_DAP_STEP_TRACE=1 env var so one flag turns on
    // both halves; written to this TEST PROCESS's own Console.Error (a different
    // process from the child, so DapTransport's write lock over on that side plays no
    // role here — xUnit/CI capture this process's stderr independently).
    private static readonly bool _traceEnabled = Environment.GetEnvironmentVariable("AL_DAP_STEP_TRACE") == "1";
    // Own instance's trace lines, appended here as well as written to Console.Error:
    // vstest's per-test console capture SHOULD surface plain Console.Error output in a
    // failed test's report, but that path isn't something this repo already has
    // end-to-end proof of in CI, whereas embedding these lines directly into the
    // TimeoutException's own message text (alongside the existing "--- stdout/stderr
    // ---" dump of the CHILD process) is the exact mechanism already CONFIRMED to
    // reach the CI job log for this class of failure (see the #2070 PR description's
    // captured CI logs). Belt and suspenders: do both, trust the one already proven.
    private readonly StringBuilder _clientTrace = new();

    private void Trace(string msg)
    {
        if (!_traceEnabled) return;
        // InvariantCulture, not the interpolated ":" format-string shorthand — ":" in a
        // custom DateTime format is the CURRENT CULTURE's time-separator placeholder,
        // and this must render byte-identically to AlDapSession.Trace's own wall-clock
        // stamp (same InvariantCulture call there) for the two traces to line up on
        // one timeline.
        var wall = DateTime.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"[dap-client-trace] wall={wall}Z {msg}";
        Console.Error.WriteLine(line);
        lock (_clientTrace) _clientTrace.AppendLine(line);
    }

    private string ClientTrace { get { lock (_clientTrace) return _clientTrace.ToString(); } }

    private DapClient(Process process, TcpClient tcp, DapTransport transport, StringBuilder stdout, StringBuilder stderr)
    {
        _process = process;
        _tcp = tcp;
        _transport = transport;
        _stdout = stdout;
        _stderr = stderr;
    }

    /// <summary>Starts al-runner --dap on a free loopback port and connects to it,
    /// retrying the connect until the runner's own "[dap] listening on" line has
    /// appeared on stdout (readiness is signalled there — --dap does NOT redirect
    /// Console.Out to stderr the way --server does, see Program.cs's --dap block)
    /// or <paramref name="readyTimeout"/> elapses. <paramref name="extraEnv"/> is set on
    /// the CHILD process only (never Environment.SetEnvironmentVariable on the current
    /// process, which would leak into whatever other test happens to run concurrently
    /// in the same shared test host) — issue #2070's watchdog-vs-pause regression test
    /// uses it to shrink AL_RUNNER_TEST_TIMEOUT_SEC so that repro stays a
    /// deterministic few seconds instead of needing a real wait past the 60s
    /// default.</summary>
    public static async Task<DapClient> StartAsync(
        string bundleDir, TimeSpan? readyTimeout = null, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var port = GetFreeLoopbackPort();
        var argList = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg +
            $" --dap {port} \"{bundleDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argList.ToString(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        var proc = Process.Start(psi)!;
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        var listeningTcs = new TaskCompletionSource();

        // ONE reader task per stream for the process's entire lifetime — diagnosed
        // during #2070: this used to start a SECOND, independent reader on the same
        // stream after the readiness handoff below ("keep draining after handoff
        // too"), and StreamReader offers no way to run two concurrent ReadLineAsync
        // loops safely: whichever task's read happened to win a given line stole it
        // for its OWN (different) StringBuilder, so a random subset of every
        // process's real stdout/stderr was permanently invisible to callers reading
        // .StdOut/.StdErr — INCLUDING "[dap] client connected." and every
        // AL_DAP_STEP_TRACE line, which is why every diagnostic dump collected while
        // chasing #2070 looked truncated immediately after "[dap] listening on..."
        // even on runs that had clearly progressed much further. A single persistent
        // reader per stream, checked inline for the readiness marker as it goes,
        // both fixes that loss and is simpler.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                lock (stderr) stderr.AppendLine(line);
        });
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                lock (stdout) stdout.AppendLine(line);
                if (line.Contains("[dap] listening on")) listeningTcs.TrySetResult();
            }
        });

        var timeout = readyTimeout ?? TimeSpan.FromSeconds(120);
        var completed = await Task.WhenAny(listeningTcs.Task, Task.Delay(timeout));
        if (completed != listeningTcs.Task)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException(
                $"al-runner --dap did not report listening within {timeout.TotalSeconds:F0}s.\n" +
                $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }

        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var transport = new DapTransport(tcp.GetStream(), tcp.GetStream());

        return new DapClient(proc, tcp, transport, stdout, stderr);
    }

    /// <summary>Bytes the OS has already received and buffered on this connection but
    /// that our own StreamReader/NetworkStream hasn't been scheduled to read yet — see
    /// the GIVEUP diagnostic in ReadOneAsync. TcpClient.Available can throw if the
    /// socket is already closed/disposed by the time this runs (e.g. a racing
    /// Detach()/process exit); that's not itself informative for THIS diagnostic, so
    /// report it as -1 rather than letting it mask the TimeoutException being built.</summary>
    private int SafeSocketAvailable()
    {
        try { return _tcp.Available; }
        catch { return -1; }
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Sends a DAP request and returns its seq (for matching against the
    /// eventual response's request_seq via <see cref="ReadUntilResponseAsync"/>).</summary>
    public int SendRequest(string command, object? arguments = null)
    {
        var seq = _transport.WriteRequest(command, arguments);
        Trace($"SEND {command} seq={seq}");
        return seq;
    }

    // Issue #2070's actual root cause, found AFTER the watchdog race (real, fixed) and
    // the Stopped-handler exception swallow (real, fixed) both turned out NOT to be
    // what any concrete local reproduction showed: DAP responses and events are two
    // INDEPENDENT streams that interleave by protocol design, and every call site in
    // this file used to call ReadUntilResponseAsync WITHOUT an `events` list — so any
    // event that arrived while waiting for a response was read off the socket and then
    // dropped on the floor by `events?.Add(root)` on a null list. Concretely, for
    // "next"/"stepIn"/"stepOut": handling the command releases the paused AL thread,
    // which is then free to run, qualify, and write its own "stopped" event — a race
    // against the DAP loop thread writing the command's OWN response. If the AL thread
    // wins, the wire order is "stopped" event FIRST, command response SECOND.
    // ReadUntilResponseAsync reads the "stopped" event, throws it away (no events
    // list), reads the response, returns — and the test's very next call,
    // ReadUntilEventAsync("stopped"), is now waiting for a SECOND "stopped" event that
    // will never be sent, hence the 60s timeout with socket.Available=0 (the event
    // really was delivered — and consumed and discarded) and a perfectly healthy
    // server (it did everything right; see AlDapSession.Trace / Program.cs's
    // STOPPED-HANDLER trace, both of which show a clean ARM/EVAL/FIRE/Walk/WriteEvent
    // sequence in every local reproduction of this hang). It only ever hits the step
    // tests, never the initial breakpoint reached via configurationDone, because there
    // the response is written before the AL thread starts running at all — no race.
    //
    // The fix is NOT to pass an `events` list at every call site — that only narrows
    // the trap for whichever call remembered to. A real DAP client treats events as a
    // durable, independent stream: anything that isn't the message currently being
    // waited for is queued, not discarded, and later reads drain the queue before
    // touching the socket.
    //
    // CAUGHT WHILE BUILDING THIS: a first version had both methods dequeue from
    // _pendingEvents and unconditionally re-enqueue a non-matching item back onto the
    // SAME queue, in the SAME loop iteration, with no socket I/O in between. With
    // exactly one item queued (the common case) that dequeue-then-requeue is a net
    // no-op that repeats at CPU-bound spin speed — no real wait, no forward progress —
    // and blew up a StringBuilder from the accompanying Trace() calls before the
    // nominal timeout's wall-clock deadline could even be reached. Fixed by splitting
    // each method into two phases: first drain whatever was ALREADY queued, bounded by
    // a snapshot of the queue's length taken before the scan starts (so a re-queued
    // miss is never reconsidered within the same phase-1 pass); only once that snapshot
    // is exhausted does phase 2 fall through to blocking socket reads, which is the
    // only phase allowed to burn real wall-clock time.
    private readonly Queue<JsonElement> _pendingEvents = new();

    /// <summary>Reads messages until the response to <paramref name="requestSeq"/>
    /// arrives, returning it. Any events seen along the way — whether already sitting
    /// in <see cref="_pendingEvents"/> from an earlier read or freshly read off the
    /// socket — are appended to <paramref name="events"/> if given, and (unconditionally)
    /// left in <see cref="_pendingEvents"/> so a later <see cref="ReadUntilEventAsync"/>
    /// still sees them even when no `events` list is given here. A response is never
    /// queued (by construction, only "event"-typed messages are), so phase 1 can only
    /// ever collect for `events`, never satisfy the wait itself — it still has to run
    /// so already-queued events are not skipped when a caller wants to see them.</summary>
    public async Task<JsonElement> ReadUntilResponseAsync(int requestSeq, List<JsonElement>? events = null, TimeSpan? timeout = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(30);

        var alreadyQueued = _pendingEvents.Count;
        for (var i = 0; i < alreadyQueued; i++)
        {
            var queuedRoot = _pendingEvents.Dequeue();
            events?.Add(queuedRoot);
            _pendingEvents.Enqueue(queuedRoot);
        }

        var deadline = DateTime.UtcNow + t;
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(t);
            var root = msg.Raw.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "response" && root.TryGetProperty("request_seq", out var rs) && rs.GetInt32() == requestSeq)
                return root;
            if (type == "event")
            {
                events?.Add(root);
                var evName = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : "?";
                Trace($"QUEUE event={evName} arrived while waiting for response to seq={requestSeq} — not dropped");
                _pendingEvents.Enqueue(root);
            }
        }
        throw new TimeoutException($"no response to request seq {requestSeq} within timeout.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}\n--- client trace ---\n{ClientTrace}");
    }

    /// <summary>Reads messages until an event named <paramref name="eventName"/>
    /// arrives, returning its body. Used to wait for e.g. "stopped". Every event seen
    /// along the way (including the terminal one) is appended to <paramref
    /// name="allEvents"/> if given, so a caller can assert on what did NOT arrive (e.g.
    /// "no 'stopped' event fired") without a second read loop. Phase 1 scans whatever
    /// is ALREADY in <see cref="_pendingEvents"/> — bounded to a snapshot of its length
    /// so a non-matching item is examined exactly once per call, never spun on — before
    /// phase 2 falls through to blocking socket reads.</summary>
    public async Task<JsonElement> ReadUntilEventAsync(string eventName, TimeSpan? timeout = null, List<JsonElement>? allEvents = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(60);

        var alreadyQueued = _pendingEvents.Count;
        for (var i = 0; i < alreadyQueued; i++)
        {
            var queuedRoot = _pendingEvents.Dequeue();
            allEvents?.Add(queuedRoot);
            var queuedEventName = queuedRoot.TryGetProperty("event", out var qEvEl) ? qEvEl.GetString() : null;
            if (queuedEventName == eventName) return queuedRoot;
            _pendingEvents.Enqueue(queuedRoot);
        }

        var deadline = DateTime.UtcNow + t;
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(t);
            var root = msg.Raw.RootElement;
            if (root.GetProperty("type").GetString() != "event") continue;
            allEvents?.Add(root);
            var thisEventName = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;
            if (thisEventName == eventName)
                return root;
            // A different event than the one being awaited right now — re-queue it
            // rather than drop it, same principle as ReadUntilResponseAsync above (a
            // second step command in a row, each waiting on its own "stopped", is the
            // same shape of race one level up).
            Trace($"QUEUE event={thisEventName ?? "?"} arrived while waiting for event={eventName} — not dropped");
            _pendingEvents.Enqueue(root);
        }
        throw new TimeoutException($"event '{eventName}' did not arrive within {t.TotalSeconds:F0}s.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}\n--- client trace ---\n{ClientTrace}");
    }

    /// <summary>
    /// Issue #2070: a per-read timeout that genuinely fires (nothing ever arrives) used
    /// to surface as a bare <see cref="OperationCanceledException"/> from the awaited
    /// <c>ReadMessageAsync</c> — thrown straight out of this method, past every dump-
    /// the-stdout/stderr TimeoutException the two callers above construct, because
    /// those are only ever reached when the read LOOP's own deadline is checked between
    /// successfully-read messages, never when a single read blocks for its whole
    /// timeout. The result: every "the stopped event never arrived" CI failure carried
    /// zero diagnostic content (no stdout, no stderr, no AL_DAP_STEP_TRACE=1 trace) —
    /// exactly the shape observed in issue #2070's saved failure logs. Converting the
    /// cancellation into the same dump-bearing TimeoutException callers already expect
    /// means the NEXT genuine hang's runner-side stderr (including the step trace) is
    /// actually visible in the test failure message instead of silently discarded.
    /// </summary>
    private async Task<DapIncomingMessage> ReadOneAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        DapIncomingMessage? msg;
        try
        {
            msg = await _transport.ReadMessageAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Coordinator review on PR #2076: "the server did its logic correctly" and
            // "the bytes never arrived" produce IDENTICAL evidence in a bare GIVEUP —
            // a client that sent, waited, and saw nothing. Two more readings, taken at
            // the exact moment of giveup, turn that ambiguity into a real answer:
            //
            // 1. Bytes already sitting in the OS socket buffer. TcpClient.Available
            //    (Socket.Available under it) counts bytes the kernel has ALREADY
            //    received and buffered, independent of whether OUR StreamReader/
            //    NetworkStream has been scheduled to read them. If this is > 0 at
            //    giveup, the "stopped" bytes truly arrived and it is our own read
            //    continuation that never got CPU time — confirms starvation, not a
            //    delivery failure. If it's 0, the bytes never got here at all and the
            //    cause is elsewhere (server write, network stack, something else).
            // 2. ThreadPool health + a live latency probe. ThreadPool.ThreadCount /
            //    PendingWorkItemCount describe the pool's OWN view of its queue depth;
            //    a genuinely healthy pool with a deep queue can still under-report
            //    "starved" by those two numbers alone. Actually measuring how long a
            //    trivial `await Task.Delay(1)` takes right now is the ground truth: a
            //    1ms delay completing in low milliseconds means the pool is fine and
            //    something else stalled; taking seconds proves pool starvation directly
            //    rather than inferring it from a bare 60s timeout.
            var socketAvailable = SafeSocketAvailable();
            var poolThreads = System.Threading.ThreadPool.ThreadCount;
            var poolPending = System.Threading.ThreadPool.PendingWorkItemCount;
            var probeSw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(1).ConfigureAwait(false);
            probeSw.Stop();
            // InvariantCulture explicitly for the same reason the wall-clock stamp above
            // needs it: ".ToString("F1")" via interpolation uses CURRENT CULTURE's
            // decimal separator, which rendered "1,1" instead of "1.1" on this exact
            // machine's locale while building this — caught by eye, not by design.
            var probeMs = probeSw.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            Trace($"GIVEUP waited {timeout.TotalSeconds:F0}s for the next message, nothing arrived — " +
                  $"socket.Available={socketAvailable} threadPool.ThreadCount={poolThreads} " +
                  $"threadPool.PendingWorkItemCount={poolPending} Task.Delay(1)ActualMs={probeMs}");
            // Give the background stdout/stderr drain loops (started in StartAsync,
            // reading proc.Standard{Output,Error}.ReadLineAsync() in a loop) one
            // scheduling quantum to catch up before snapshotting them: under the exact
            // CPU contention this timeout is meant to survive, those loops are
            // themselves delayed, and a snapshot taken with zero grace can under-report
            // lines the child process already wrote (diagnosed reproducing #2070 under
            // load: the dump cut off mid-startup even though the child had clearly
            // progressed much further, going by the exception's own elapsed time).
            await Task.Delay(500).ConfigureAwait(false);
            throw new TimeoutException(
                $"--dap read timed out after {timeout.TotalSeconds:F0}s.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}\n--- client trace ---\n{ClientTrace}");
        }
        if (msg == null)
            throw new Exception($"--dap connection closed unexpectedly.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
        return msg;
    }

    public async ValueTask DisposeAsync()
    {
        try { _transport.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        _process.Dispose();
    }
}
