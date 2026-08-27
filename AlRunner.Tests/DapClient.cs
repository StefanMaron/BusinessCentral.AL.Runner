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
///
/// The response/event demultiplexing (queueing, phased drain, the #2070 race this
/// exists to prevent) lives in the shared <see cref="DapClientBase"/> — see its
/// header comment. This class supplies only what's specific to the TCP transport:
/// spawning + connecting, and the socket-available/ThreadPool diagnostics attached
/// to a genuine read timeout.
/// </summary>
public sealed class DapClient : DapClientBase
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly Process _process;
    private readonly TcpClient _tcp;
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

    protected override void Trace(string msg)
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

    protected override string DiagnosticDump() =>
        $"--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}\n--- client trace ---\n{ClientTrace}";

    private DapClient(Process process, TcpClient tcp, DapTransport transport, StringBuilder stdout, StringBuilder stderr)
        : base(transport)
    {
        _process = process;
        _tcp = tcp;
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
    protected override async Task<DapIncomingMessage> ReadOneAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        DapIncomingMessage? msg;
        try
        {
            msg = await Transport.ReadMessageAsync(cts.Token);
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

    public override async ValueTask DisposeAsync()
    {
        try { Transport.Dispose(); } catch { }
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
