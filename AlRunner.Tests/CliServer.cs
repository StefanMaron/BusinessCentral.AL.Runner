using System.Diagnostics;

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
