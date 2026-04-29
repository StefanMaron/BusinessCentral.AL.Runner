using System.Diagnostics;
using System.Text.Json;

namespace AlRunner.Tests;

/// <summary>
/// Helper to start al-runner in --server mode and communicate via stdin/stdout.
/// Each line sent is a JSON request; each line received is a JSON response.
/// </summary>
public class CliServer : IAsyncDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly Process _process;

    private CliServer(Process process)
    {
        _process = process;
    }

    public int ExitCode => _process.ExitCode;

    // Derive the current TFM moniker (e.g. "net8.0" or "net9.0") from the running CLR version
    // so that dotnet run targets the same framework that is executing the test.
    private static string CurrentFramework()
    {
        var v = System.Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    public static async Task<CliServer> StartAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --framework {CurrentFramework()} --project \"{ProjectPath}\" -- --server",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot
        };

        var proc = Process.Start(psi)!;

        // Wait for the server to signal readiness (first line on stdout)
        var readyLine = await proc.StandardOutput.ReadLineAsync();
        if (readyLine == null)
            throw new Exception("Server process exited before signaling readiness");

        return new CliServer(proc);
    }

    /// <summary>Send a JSON request line and read the JSON response line.</summary>
    public async Task<string> SendAsync(string jsonRequest)
    {
        await _process.StandardInput.WriteLineAsync(jsonRequest);
        await _process.StandardInput.FlushAsync();

        var response = await _process.StandardOutput.ReadLineAsync();
        return response ?? throw new Exception("Server closed stdout before responding");
    }

    /// <summary>
    /// Send a request and accumulate response lines until a <c>{"type":"summary"}</c>
    /// line arrives. Used for the protocol-v2 streaming runtests command.
    ///
    /// Non-JSON lines (should not occur for a well-behaved server, but are tolerated for
    /// debugging) are still appended to the returned list and skipped for the terminator
    /// check. The terminator line is always the last entry in the returned list.
    /// </summary>
    public async Task<List<string>> SendRequestStreamingAsync(string jsonRequest)
    {
        await _process.StandardInput.WriteLineAsync(jsonRequest);
        await _process.StandardInput.FlushAsync();

        var lines = new List<string>();
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync();
            if (line == null)
                throw new InvalidOperationException("stdout closed before summary");
            lines.Add(line);
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "summary")
                {
                    return lines;
                }
            }
            catch (JsonException)
            {
                // Tolerate the rare non-JSON line — the dispatcher should never emit one,
                // but if it does we don't want the test helper to deadlock waiting for a
                // terminator that will never arrive.
            }
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
                _process.Kill();
                await _process.WaitForExitAsync();
            }
            catch { }
        }
        _process.Dispose();
    }
}
