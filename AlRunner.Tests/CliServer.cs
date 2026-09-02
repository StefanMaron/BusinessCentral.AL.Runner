using System.Diagnostics;
using System.Text;

namespace AlRunner.Tests;

/// <summary>
/// Helper to start al-runner in --server mode and communicate via stdin/stdout.
/// Each line sent is a JSON request; each line received is a JSON response.
///
/// v2 notes:
///  - The runner prints a lot of warm-up noise; in --server mode that all goes
///    to stderr so stdout carries ONLY the newline-delimited JSON protocol.
///    We drain stderr on a background task so its pipe buffer never fills and
///    deadlocks the warm-up (which can emit &gt;64 KB before readiness).
///  - On a cold Cecil rewrite the runner re-execs itself once; the re-exec
///    inherits our stdio handles, so the protocol still flows through this pipe.
/// </summary>
public sealed class CliServer : IAsyncDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    /// <summary>
    /// #1804: how many times <see cref="StartAsync"/> has actually called
    /// <c>Process.Start</c> in this test-worker process, since assembly load.
    /// Process-wide, so it is a diagnostic only — xUnit runs different test
    /// classes' spawns concurrently with each other (different collections),
    /// so a delta read around one test's own spawns can be inflated by another
    /// class spawning at the same moment. The actual proving assertion in
    /// SharedCliServerTests.cs uses <see cref="SharedCliServer.SpawnCount"/>
    /// instead, which is scoped to one fixture instance and immune to that.
    /// </summary>
    public static int StartCount => _startCount;
    private static int _startCount;

    private CliServer(Process process)
    {
        _process = process;
    }

    public int ExitCode => _process.ExitCode;

    /// <summary>OS process id of the server subprocess. #1888: lets a caller correlate
    /// phase-log rows (which carry "pid") with the exact process this handle wraps.</summary>
    public int Pid => _process.Id;

    /// <summary>Captured stderr so far — surfaced in assertion messages when the protocol stalls.</summary>
    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

    /// <summary>
    /// #2377: how many characters of stderr have been captured so far — a marker to
    /// slice ONE request's diagnostics out of a shared server's cumulative stderr.
    ///
    /// A class-shared server serves many requests through one pipe, so
    /// <see cref="StdErr"/> is the concatenation of every request's output. A test
    /// converted from "spawn the CLI once, assert on its whole console dump" needs the
    /// dump for ITS request only — otherwise `Assert.DoesNotContain("[layered] WROTE B")`
    /// would match a line the PREVIOUS fact legitimately produced, and the converted test
    /// would fail for a reason that has nothing to do with what it proves.
    ///
    /// Take the mark immediately before <c>SendRequestStreamingAsync</c>, read the slice
    /// with <see cref="StdErrSinceAsync"/> after.
    /// </summary>
    public int StdErrMark { get { lock (_stderr) return _stderr.Length; } }

    /// <summary>
    /// The stderr captured since <paramref name="mark"/>, once <paramref name="anchor"/>
    /// appears in it — or a <see cref="TimeoutException"/> naming both.
    ///
    /// The wait is the point, and skipping it is how this goes flaky. stdout (the JSON
    /// protocol) and stderr (every diagnostic a converted test asserts on) are two
    /// separate pipes drained by two different threads, so reading the terminal
    /// <c>{"type":"summary"}</c> line establishes NOTHING about how much of that
    /// request's stderr the background drain task has appended yet. Measured on a loaded
    /// box the last stderr line of a request lands 0.05-5.9s BEFORE its summary — i.e.
    /// usually early enough that an unsynchronised read would pass, which is exactly the
    /// shape of a test that fails once a month on CI and nowhere else.
    ///
    /// <paramref name="anchor"/> must be a string the runner emits AFTER everything the
    /// caller intends to assert on — for the layered pre-pass, its
    /// "[layered] pre-built N impl package(s)" trailer, which follows the per-impl
    /// WROTE/HIT lines. That ordering is what makes a NEGATIVE assertion
    /// (<c>DoesNotContain</c>) on the returned slice sound: without an anchor emitted
    /// after the line being excluded, "not there yet" and "never emitted" are the same
    /// observation.
    /// </summary>
    public async Task<string> StdErrSinceAsync(int mark, string anchor, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var slice = StdErrSince(mark);
            if (slice.Contains(anchor, StringComparison.Ordinal)) return slice;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"stderr anchor '{anchor}' never appeared in this request's slice.\n" +
                    $"--- slice since mark {mark} ---\n{slice}");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// The raw stderr slice since <paramref name="mark"/>, with no wait. Only sound when
    /// the caller has ALREADY synchronised on an anchor via <see cref="StdErrSinceAsync"/>
    /// (e.g. to take a second, wider slice), or when the assertion is positive and the
    /// caller is prepared for it to be racy. Prefer <see cref="StdErrSinceAsync"/>.
    /// </summary>
    public string StdErrSince(int mark)
    {
        lock (_stderr)
            return mark >= _stderr.Length ? string.Empty : _stderr.ToString(mark, _stderr.Length - mark);
    }

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    /// <param name="extraArgs">Extra CLI args after --server (e.g. --cache, --package-cache).</param>
    /// <param name="extraEnv">
    /// Extra environment variables set on THIS child process only (via
    /// <see cref="ProcessStartInfo.EnvironmentVariables"/>, never via
    /// <see cref="Environment.SetEnvironmentVariable"/> on the current xUnit
    /// worker process) — so a test-only knob like
    /// AL_RUNNER_TEST_BARRIER_DIR (#1845) cannot leak into any OTHER test's
    /// server subprocess started concurrently on a shared xUnit worker.
    /// </param>
    public static async Task<CliServer> StartAsync(IEnumerable<string>? extraArgs = null, TimeSpan? readyTimeout = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var argList = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + " --server");
        if (extraArgs != null)
            foreach (var a in extraArgs)
                argList.Append(' ').Append(a.Contains(' ') ? $"\"{a}\"" : a);

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
            foreach (var kv in extraEnv)
                psi.EnvironmentVariables[kv.Key] = kv.Value;

        var proc = Process.Start(psi)!;
        System.Threading.Interlocked.Increment(ref _startCount);

        var server = new CliServer(proc);
        // Drain stderr on a background task so its pipe buffer never fills and
        // deadlocks the warm-up. The task ends when the process exits (EOF).
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                lock (server._stderr) server._stderr.AppendLine(line);
        });

        // Wait for {"ready":true} as the first stdout line, with a timeout so a
        // hung warm-up surfaces as a test failure (with stderr) rather than a hang.
        var readyTask = proc.StandardOutput.ReadLineAsync();
        var timeout = readyTimeout ?? TimeSpan.FromSeconds(120);
        var completed = await Task.WhenAny(readyTask, Task.Delay(timeout));
        if (completed != readyTask)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException(
                $"Server did not signal readiness within {timeout.TotalSeconds:F0}s.\n--- stderr ---\n{server.StdErr}");
        }
        var readyLine = await readyTask;
        if (readyLine == null)
            throw new Exception($"Server exited before signaling readiness.\n--- stderr ---\n{server.StdErr}");
        if (!readyLine.Contains("\"ready\""))
            throw new Exception($"First stdout line was not the readiness signal: '{readyLine}'\n--- stderr ---\n{server.StdErr}");

        return server;
    }

    /// <summary>Send a JSON request line and read the JSON response line.</summary>
    public async Task<string> SendAsync(string jsonRequest, TimeSpan? timeout = null)
    {
        await _process.StandardInput.WriteLineAsync(jsonRequest);
        await _process.StandardInput.FlushAsync();

        var readTask = _process.StandardOutput.ReadLineAsync();
        var t = timeout ?? TimeSpan.FromSeconds(120);
        var completed = await Task.WhenAny(readTask, Task.Delay(t));
        if (completed != readTask)
            throw new TimeoutException(
                $"No response within {t.TotalSeconds:F0}s to: {jsonRequest}\n--- stderr ---\n{StdErr}");
        return await readTask
            ?? throw new Exception($"Server closed stdout before responding to: {jsonRequest}\n--- stderr ---\n{StdErr}");
    }

    /// <summary>
    /// Send a JSON request and read lines until the streaming terminator: either
    /// the protocol-v2 <c>{"type":"summary"}</c> line (runTests' streaming shape —
    /// see #1641), or the first line that isn't <c>type: test</c>/<c>progress</c>
    /// (a single-line error/ack response from a command that doesn't stream).
    /// Returns every line read, in order, including the terminator.
    /// </summary>
    public async Task<List<string>> SendRequestStreamingAsync(string jsonRequest, TimeSpan? timeout = null)
    {
        await _process.StandardInput.WriteLineAsync(jsonRequest);
        await _process.StandardInput.FlushAsync();

        var t = timeout ?? TimeSpan.FromSeconds(120);
        var lines = new List<string>();
        while (true)
        {
            var readTask = _process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(t));
            if (completed != readTask)
                throw new TimeoutException(
                    $"No response within {t.TotalSeconds:F0}s to: {jsonRequest}\n--- stderr ---\n{StdErr}");
            var line = await readTask
                ?? throw new Exception($"Server closed stdout before completing: {jsonRequest}\n--- stderr ---\n{StdErr}");
            lines.Add(line);

            string? type = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var tEl))
                    type = tEl.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON (shouldn't happen) — treat as terminal so callers see it.
            }
            if (type == "summary") break;
            if (type != "test" && type != "progress") break; // single-line error/ack response
        }
        return lines;
    }

    /// <summary>
    /// Send a <c>runTests</c> request and, the moment the FIRST <c>{"type":"test"}</c>
    /// line lands on stdout, push a <c>{"command":"cancel"}</c> request back on stdin
    /// (see #1641/v1 #1613-#1614). Keeps reading until the terminating
    /// <c>{"type":"summary"}</c> line and returns the full transcript (test/summary/ack
    /// lines, in the order the server emitted them — the ack can land interleaved with
    /// still-streaming test lines, since it's answered by a side-channel thread
    /// independent of the runtests handler) along with the ack line for the cancel.
    ///
    /// Exercises the mid-run cancel path end to end: it only proves anything if the
    /// server's stdin-reader thread is willing to read and answer another request line
    /// while `runtests` is still streaming on the main dispatch thread.
    /// </summary>
    /// <param name="onAckReceived">
    /// Invoked synchronously exactly once, right after the cancel's ack line is
    /// observed, before this method reads any further stdout lines. #1845:
    /// ServerCancelTests uses this to release its test-only barrier (see
    /// AlRunner.Infrastructure.TestBarrier) only once the ack is confirmed —
    /// i.e. only once "cancel arrived while a run was active" is already proven,
    /// not merely "cancel was sent".
    /// </param>
    public async Task<(List<string> Lines, string? AckLine)> SendRequestAndCancelAfterFirstTestAsync(
        string jsonRequest, TimeSpan? timeout = null, Action? onAckReceived = null)
    {
        await _process.StandardInput.WriteLineAsync(jsonRequest);
        await _process.StandardInput.FlushAsync();

        var t = timeout ?? TimeSpan.FromSeconds(120);
        var lines = new List<string>();
        string? ackLine = null;
        bool cancelSent = false;

        while (true)
        {
            var readTask = _process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(t));
            if (completed != readTask)
                throw new TimeoutException(
                    $"No response within {t.TotalSeconds:F0}s to: {jsonRequest}\n--- stderr ---\n{StdErr}");
            var line = await readTask
                ?? throw new Exception($"Server closed stdout before completing: {jsonRequest}\n--- stderr ---\n{StdErr}");
            lines.Add(line);

            string? type = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var tEl))
                    type = tEl.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
                // Tolerate a non-JSON line (should not happen) — it doesn't terminate
                // the stream and doesn't carry an ack/summary signal we need to act on.
            }

            // Fire cancel the instant the FIRST test event is observed — proves cts
            // already exists (OnTestComplete can only fire after HandleServerRunTests
            // published it), so this cancel is never a "no active run yet" false noop.
            if (!cancelSent && type == "test")
            {
                cancelSent = true;
                await _process.StandardInput.WriteLineAsync("{\"command\":\"cancel\"}");
                await _process.StandardInput.FlushAsync();
            }
            if (type == "ack" && cancelSent && ackLine == null)
            {
                ackLine = line;
                onAckReceived?.Invoke();
            }
            if (type == "summary")
                return (lines, ackLine);
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
            }
            catch { }
        }
        _process.Dispose();
    }
}
