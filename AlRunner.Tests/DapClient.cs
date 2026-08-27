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
    public int SendRequest(string command, object? arguments = null) => _transport.WriteRequest(command, arguments);

    /// <summary>Reads messages until the response to <paramref name="requestSeq"/>
    /// arrives, returning it. Any events seen along the way are appended to
    /// <paramref name="events"/> if given (so a caller can inspect e.g. an
    /// `initialized` event that arrives between the `initialize` request and its
    /// response, without a second read loop).</summary>
    public async Task<JsonElement> ReadUntilResponseAsync(int requestSeq, List<JsonElement>? events = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(timeout ?? TimeSpan.FromSeconds(30));
            var root = msg.Raw.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "response" && root.TryGetProperty("request_seq", out var rs) && rs.GetInt32() == requestSeq)
                return root;
            if (type == "event") events?.Add(root);
        }
        throw new TimeoutException($"no response to request seq {requestSeq} within timeout.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
    }

    /// <summary>Reads messages until an event named <paramref name="eventName"/>
    /// arrives, returning its body. Used to wait for e.g. "stopped". Every event
    /// seen along the way (including the terminal one) is appended to <paramref
    /// name="allEvents"/> if given, so a caller can assert on what did NOT arrive
    /// (e.g. "no 'stopped' event fired") without a second read loop.</summary>
    public async Task<JsonElement> ReadUntilEventAsync(string eventName, TimeSpan? timeout = null, List<JsonElement>? allEvents = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(60);
        var deadline = DateTime.UtcNow + t;
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(t);
            var root = msg.Raw.RootElement;
            if (root.GetProperty("type").GetString() != "event") continue;
            allEvents?.Add(root);
            if (root.TryGetProperty("event", out var ev) && ev.GetString() == eventName)
                return root;
        }
        throw new TimeoutException($"event '{eventName}' did not arrive within {t.TotalSeconds:F0}s.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
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
                $"--dap read timed out after {timeout.TotalSeconds:F0}s.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
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
