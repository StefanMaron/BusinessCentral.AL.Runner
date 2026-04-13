using System.Diagnostics;

namespace AlRunner.Tests;

/// <summary>
/// Helper to invoke the al-runner CLI as a child process and capture results.
/// </summary>
public static class CliRunner
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    public record CliResult(int ExitCode, string StdOut, string StdErr);

    // Derive the current TFM moniker (e.g. "net8.0" or "net9.0") from the running CLR version.
    private static string CurrentFramework()
    {
        var v = System.Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    /// <summary>
    /// Run al-runner with the given arguments via "dotnet run".
    /// </summary>
    public static async Task<CliResult> RunAsync(string args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --framework {CurrentFramework()} --project \"{ProjectPath}\" -- {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot
        };

        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeoutMs);

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

        await proc.WaitForExitAsync(cts.Token);

        return new CliResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Run al-runner on a test case directory under tests/{name}/.
    /// </summary>
    public static Task<CliResult> RunTestCaseAsync(string testCaseName, string extraArgs = "")
    {
        var srcDir = Path.Combine(RepoRoot, "tests", testCaseName, "src");
        var testDir = Path.Combine(RepoRoot, "tests", testCaseName, "test");
        var args = $"\"{srcDir}\" \"{testDir}\"";
        if (!string.IsNullOrEmpty(extraArgs))
            args = $"{extraArgs} {args}";
        return RunAsync(args);
    }
}
