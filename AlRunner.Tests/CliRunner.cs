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

    /// <summary>
    /// Resolves a test case name to its directory under tests/.
    /// Searches bucket-*, stubs/, and excluded/ subdirectories at any depth
    /// (the AL test buckets group suites under category subfolders).
    ///
    /// Throws InvalidOperationException if the name is ambiguous (matches more
    /// than one suite across categories) so cross-category name clashes surface
    /// instead of silently shadowing.
    /// </summary>
    public static string FindTestCase(string testCaseName)
    {
        var testsRoot = Path.Combine(RepoRoot, "tests");
        var matches = new List<string>();
        foreach (var bucket in Directory.GetDirectories(testsRoot).OrderBy(d => d))
        {
            // Direct child (covers tests/stubs/<name> and tests/excluded/<name>)
            var direct = Path.Combine(bucket, testCaseName);
            if (Directory.Exists(direct))
                matches.Add(direct);

            // Category subfolder (covers tests/bucket-*/category/<name>)
            foreach (var category in Directory.GetDirectories(bucket).OrderBy(d => d))
            {
                var nested = Path.Combine(category, testCaseName);
                if (Directory.Exists(nested))
                    matches.Add(nested);
            }
        }

        if (matches.Count == 0)
        {
            throw new DirectoryNotFoundException(
                $"Test case '{testCaseName}' not found under {testsRoot}/*/");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Test case name '{testCaseName}' is ambiguous; matched {matches.Count} suites: " +
                string.Join(", ", matches));
        }

        return matches[0];
    }

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
        var caseDir = FindTestCase(testCaseName);
        var srcDir = Path.Combine(caseDir, "src");
        var testDir = Path.Combine(caseDir, "test");
        var args = $"\"{srcDir}\" \"{testDir}\"";
        if (!string.IsNullOrEmpty(extraArgs))
            args = $"{extraArgs} {args}";
        return RunAsync(args);
    }
}
