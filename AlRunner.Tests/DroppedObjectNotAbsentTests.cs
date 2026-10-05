// DroppedObjectNotAbsentTests — #5339. RUNNER-MECHANISM: what the runner does with a test that reaches, by id, an
// object of ANOTHER bundle that was dropped from its compile or never compiled. The BC claim (a missing object raises
// BC's "object does not exist" AL error, which asserterror catches) is measured upstream, corpus codeunits "Test
// Codeunit Run Missing" and "Test RecordRef Open Missing"; a dropped object is the state a service tier cannot be in.
//
// A dropped object EXISTS in the source, so reading it as absent would let a bare `asserterror` pass over a compile
// error. Run alone, the tests below pass: nothing declares the ids, so they are absent and BC's error is raised.
// Listed after a bundle that dropped a codeunit (partial-lib) or never compiled (broken-lib) them, each must FAIL loudly.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DroppedObjectNotAbsentTests
{
    private const int SpawnTimeoutMs = 120_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Fixture(string name)
        => Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "MissingObjectDropped", name);

    private static string Run(params string[] bundles)
    {
        var cacheDir = TestScratch.Dir("al-runner-dropped-not-absent");
        try
        {
            var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
            foreach (var b in bundles) sb.Append(' ').Append($"\"{Fixture(b)}\"");
            sb.Append(' ').Append($"--cache \"{cacheDir}\"");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", Arguments = sb.ToString(), RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            var output = new StringBuilder();
            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(SpawnTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"al-runner did not exit within {SpawnTimeoutMs / 1000}s.");
            }
            proc.WaitForExit(); // drain the async readers, or the last lines can still be in flight (#2496)
            return output.ToString();
        }
        finally { try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ } }
    }

    private static readonly string[] Tests =
    {
        "BareAsserterror_RunDroppedCodeunit", "Guarded_RunDroppedCodeunit",
        "BareAsserterror_OpenUncompiledTable", "BareAsserterror_RunUncompiledCodeunit",
    };

    private static void AssertEveryTestFailed(string output, string which)
    {
        foreach (var t in Tests) Assert.True(output.Contains($"FAIL  \"MOD Tests\".{t}"), $"{which}: {t} must fail loudly.\n{output}");
        Assert.DoesNotContain("PASS  Codeunit72231.", output);
        // Not BC's clean error, which asserterror would have caught: the refusal is a CLR exception that escapes it.
        Assert.DoesNotContain("does not exist in the current application", output);
    }

    [SkippableFact]
    public void Control_RunAlone_EachIdIsAbsent_AndBcsErrorIsCaught()
    {
        TestArtifacts.SkipIfMissing();

        var output = Run("tests");

        Assert.Contains("Tests: 4   passed 4   failed 0", output);
    }

    /// <summary>The non-TDD path: the library's compile dropped a test codeunit nothing names, and the module runs
    /// past it (ReportNonTddEmitDrops). No bundle failed to compile here, so that path is the only thing that says so.</summary>
    [SkippableFact]
    public void ObjectsAnEarlierBundleDroppedFromItsCompile_AreNotAbsent()
    {
        TestArtifacts.SkipIfMissing();

        var output = Run("partial-lib", "tests");

        Assert.Contains("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("The module was NOT run", output);
        AssertEveryTestFailed(output, "partial-lib");
    }

    /// <summary>The control for the clause above: a dropped PROFILE declares no executable AL and cannot be reached by id,
    /// so the drop must not make an id nothing declares read as anything but absent.</summary>
    [SkippableFact]
    public void AProfileAnEarlierBundleDropped_DoesNotStopAMissingIdReadingAsAbsent()
    {
        TestArtifacts.SkipIfMissing();

        var output = Run("profile-lib", "tests");

        Assert.Contains("EMIT-EXCLUDED", output);
        Assert.Contains("Tests: 4   passed 4   failed 0", output);
    }

    /// <summary>A bundle that emitted nothing (the bundle loop's compile-failure stage).</summary>
    [SkippableFact]
    public void ObjectsOfABundleThatDidNotCompile_AreNotAbsent()
    {
        TestArtifacts.SkipIfMissing();

        var output = Run("broken-lib", "broken-table-lib", "tests");

        Assert.Contains("EMIT-ZERO", output);
        AssertEveryTestFailed(output, "broken-lib");
    }
}
