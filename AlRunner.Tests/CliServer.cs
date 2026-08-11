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

    private CliServer(Process process)
    {
        _process = process;
    }

    public int ExitCode => _process.ExitCode;

    /// <summary>Captured stderr so far — surfaced in assertion messages when the protocol stalls.</summary>
    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    /// <param name="extraArgs">Extra CLI args after --server (e.g. --cache, --package-cache).</param>
    public static async Task<CliServer> StartAsync(IEnumerable<string>? extraArgs = null, TimeSpan? readyTimeout = null)
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

        var proc = Process.Start(psi)!;

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
    public async Task<(List<string> Lines, string? AckLine)> SendRequestAndCancelAfterFirstTestAsync(
        string jsonRequest, TimeSpan? timeout = null)
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
                ackLine = line;
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
