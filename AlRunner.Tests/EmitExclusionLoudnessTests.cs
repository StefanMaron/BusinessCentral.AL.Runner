// EmitExclusionLoudnessTests — an AL object dropped from the emit must fail the run.
//
// BC's Compilation.Emit is atomic per module: one object that cannot bind takes the
// whole module down. The runner works around that by excluding the broken object and
// recompiling the rest (BcCompiler's emit-retry loop). That recovery is worth having,
// but it silently changes what the run COVERS — an excluded test codeunit contributes
// no results, so the total shrinks and every surviving test still passes.
//
// Measured on the al-language corpus: a System.app one major version behind what the
// AL compiler demanded (AL1022) cascaded into 6 excluded objects and took 7 tests out
// of the run (1904 -> 1897). The run printed nothing at default verbosity and exited 0.
// The EMIT-FAIL lines did reach Console.Error, but they carry a [BcCompiler] prefix and
// Log's FilteredWriter drops [Component]-tagged lines unless --verbose. CI compared a
// green 1897 against a green 1904 and reported success both times.
//
// Note the pre-existing PARTIAL-EMIT-DROP guard does NOT cover this: it is gated on
// `alDiagnostics.Count == 0`, and an exclusion always carries the diagnostics that
// identified the broken object, so that branch is skipped by construction.
//
// See .claude/rules/loud-failures.md — a green run that quietly covers less is exactly
// the "green test that lies" this rule exists to prevent.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests are serialized.
[Collection("server-serial")]
public sealed class EmitExclusionLoudnessTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "EmitExclusion");

    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stdCache = Path.Combine(home, ".local", "share", "al-runner", "artifacts");
        return Directory.Exists(stdCache) && Directory.EnumerateDirectories(stdCache).Any();
    }

    private static (string Output, int Exit) RunRunner()
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{FixturePath}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The fixture pairs a healthy test codeunit with one that cannot bind. The run must
    /// exit non-zero and name the dropped object — WITHOUT --verbose, because default
    /// verbosity is what CI and every developer actually reads.
    ///
    /// This test is not vacuous: it fails if the exclusion is reported only through the
    /// [BcCompiler] EMIT-FAIL line (filtered away at this verbosity), if the excluded
    /// names are omitted, or if the runner exits 0 after compiling the healthy remainder.
    /// </summary>
    [Fact]
    public void ExcludedObject_IsReportedAndFailsTheRun()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not provisioned"); return; }

        var (output, exit) = RunRunner();

        Assert.True(exit != 0,
            $"an emit exclusion drops AL objects from the module, so the run covers less than it "
            + $"claims and must NOT exit 0. exit={exit}\n{output}");

        Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);

        // The specific object must be named — "something was excluded" is not actionable.
        Assert.Contains("Emit Excl Broken", output, StringComparison.Ordinal);

        // And the report must say that tests went missing, since that is the consequence
        // a reader has to understand.
        Assert.Contains("MISSING", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction: the healthy sibling alone must compile and pass cleanly. Without
    /// this, the test above would still pass if the runner had simply broken outright on the
    /// fixture — proving nothing about exclusion detection specifically.
    /// </summary>
    [Fact]
    public void HealthyObjectsAlone_StillPass()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not provisioned"); return; }

        var tmp = Path.Combine(Path.GetTempPath(), "al-runner-emit-excl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in Directory.GetFiles(FixturePath))
            {
                if (Path.GetFileName(f) == "BrokenObject.Codeunit.al") continue; // the only broken object
                File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
            }

            var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
            args.Append(TestBuildConfig.BcVersionArg);
            args.Append($" \"{tmp}\"");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", Arguments = args.ToString(),
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            var sb = new StringBuilder();
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
            p.WaitForExit();
            string output; lock (sb) output = sb.ToString();

            Assert.True(p.ExitCode == 0, $"the healthy fixture alone must pass. exit={p.ExitCode}\n{output}");
            Assert.DoesNotContain("EMIT-EXCLUDED", output, StringComparison.Ordinal);
            Assert.Contains("Healthy_Addition_StillRuns", output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
