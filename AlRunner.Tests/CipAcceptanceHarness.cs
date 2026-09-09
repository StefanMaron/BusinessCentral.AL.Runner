// CipAcceptanceHarness — the runner spawn accept-partial-company-init's process-level arms share
// (#3561). Extracted from PartialCompanyInitAcceptanceTests when the escalation arms needed the
// same spawn: two copies of it would let the two classes drift into driving different runners.
using System.Diagnostics;
using System.Text;

namespace AlRunner.Tests;

internal static class CipAcceptanceHarness
{
    internal static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    internal static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    internal static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "CompanyInitPartial");
    /// <summary>The same bundle with one deliberately failing test, so the run earns exit 1.</summary>
    internal static readonly string FailingFixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "CompanyInitPartialFailing");

    /// <summary>The value the seam turns into the exception message, so every assertion is
    /// against text that could only have come through the real catch path.</summary>
    internal const string InjectedReason = "CIP-INJECTED-ABORT: InitSourceCodeSetup did not finish";

    internal const string AcceptedBecause =
        "this project ships without the Manufacturing dependency codeunit 2 needs; tracked in-house";

    internal sealed record Run(string Output, int Exit);

    internal static Run RunRunner(string cacheDir, string? expectationsDir,
        bool injectAbort = true, bool injectCompleted = false, bool outputJson = false,
        string[]? bundles = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{cacheDir}\"");
        if (expectationsDir != null) args.Append($" --expectations \"{expectationsDir}\"");
        if (outputJson) args.Append(" --output-json");
        foreach (var b in bundles ?? new[] { FixturePath }) args.Append($" \"{b}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        if (injectAbort) psi.Environment["AL_RUNNER_TEST_FAIL_COMPANY_INIT"] = InjectedReason;
        else psi.Environment.Remove("AL_RUNNER_TEST_FAIL_COMPANY_INIT");
        if (injectCompleted) psi.Environment["AL_RUNNER_TEST_COMPANY_INIT_COMPLETED"] = "1";

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return new Run(sb.ToString(), p.ExitCode);
    }

    internal static string NewCacheDir()
    {
        var dir = TestScratch.Dir("al-runner-cipa-cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Write a one-entry manifest directory and return its path.</summary>
    internal static string ManifestDir(string name, string entryJson)
    {
        var dir = TestScratch.Dir("al-runner-cipa-manifest-" + name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "accept-company-init.json"), "[\n" + entryJson + "\n]\n");
        return dir;
    }

    internal static string AcceptEntry(int codeunitId = 2, string codeunitName = "Company-Initialize",
        string reason = AcceptedBecause, string method = "*")
        => $$"""
          {
            "codeunitId": {{codeunitId}},
            "CodeunitName": "{{codeunitName}}",
            "Method": "{{method}}",
            "Mode": "accept-partial-company-init",
            "Reason": "{{reason}}"
          }
        """;
}
