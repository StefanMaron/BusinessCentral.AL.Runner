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

// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
// [Collection("server-serial")] and no longer are — #1809.
public sealed class EmitExclusionLoudnessTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "EmitExclusion");

    private static (string Output, int Exit) RunRunner(bool verbose = false)
        => RunRunnerOn(FixturePath, verbose);

    private static (string Output, int Exit) RunRunnerOn(
        string bundlePath, bool verbose = false, string? cacheDir = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        if (verbose) args.Append(" --verbose");
        if (cacheDir != null) args.Append($" --cache \"{cacheDir}\"");
        args.Append($" \"{bundlePath}\"");
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
    [SkippableFact]
    public void ExcludedObject_IsReportedAndFailsTheRun()
    {
        TestArtifacts.SkipIfMissing();

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
    /// Issue #2207: the EMIT-EXCLUDED message tells the user to "Re-run with --verbose for
    /// the AL diagnostics that identified them." Passing --verbose must actually produce the
    /// AL compiler diagnostic (AL0185, "Codeunit '...' is missing") that explains WHY
    /// "Emit Excl Broken" could not bind — not just the same summary line again. Before the
    /// fix, BcCompiler's emit-retry loop discarded the identifying diagnostics once the
    /// retry against the surviving objects succeeded, so re-running with --verbose printed
    /// nothing new — the exact defect this test proves fixed.
    /// </summary>
    [SkippableFact]
    public void ExcludedObject_Verbose_PrintsTheIdentifyingAlDiagnostic()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(verbose: true);

        Assert.True(exit != 0, $"exclusion must still fail the run under --verbose. exit={exit}\n{output}");
        Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);

        // The specific AL diagnostic that caused the exclusion — not just the object's name
        // (already asserted by the non-verbose test above) and not just the crash's internal
        // BC-emitter text ("Unexpected value 'None' of type 'NavTypeKind'"), which names an
        // implementation detail, not the AL-level cause a developer can act on.
        Assert.Contains("AL0185", output, StringComparison.Ordinal);
        Assert.Contains("This Codeunit Does Not Exist At All", output, StringComparison.Ordinal);
        Assert.Contains("BrokenObject.Codeunit.al", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// REVERSED by #2949, deliberately. This test used to assert the opposite — that at
    /// default verbosity the identifying diagnostic must stay OUT of the output, because
    /// "the summary line alone is the promise". That decision made the runner unusable on
    /// malformed AL: a syntactically broken table produced, at default verbosity, an
    /// emitter NullReferenceException, an AL0185 raised against a blameless sibling file,
    /// and an instruction to re-run with a flag. The one accurate account of the failure
    /// was the thing being withheld.
    ///
    /// An exclusion that is NOT all-profiles fails the run outright (`sources` is cleared,
    /// the bundle reports COMPILE FAIL). The AL errors of a failed compile already print at
    /// default verbosity everywhere else in this runner — EMIT-ZERO does it immediately
    /// below this branch, and the --server path does it too — so withholding these was the
    /// odd one out, not the rule. --verbose still adds the [Component]-tagged detail around
    /// them; it is no longer what decides whether the cause is stated at all.
    ///
    /// The profile-only exclusion keeps the --verbose gate, because that run CONTINUES and
    /// is not a failure. See BcCompilerProfileEmitCrashTests for that path.
    /// </summary>
    [SkippableFact]
    public void ExcludedObject_DefaultVerbosity_StatesTheCauseBecauseTheRunFailed()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(verbose: false);

        Assert.True(exit != 0, $"exit={exit}\n{output}");
        Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);
        Assert.Contains("AL0185", output, StringComparison.Ordinal);
        Assert.Contains("This Codeunit Does Not Exist At All", output, StringComparison.Ordinal);

        // And, having printed them, it must not also tell the reader to go and fetch them.
        Assert.DoesNotContain("Re-run with --verbose", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #3476. The dropped object is a test codeunit nothing else in the module names, so
    /// the module still RUNS: the healthy sibling's test executes, the dropped codeunit's test
    /// is reported SKIPPED, and the run still fails (exit 3) because it covers less than it
    /// discovered.
    ///
    /// Before this change the same fixture reported `0P/0F/0E across 0 tests` and COMPILE FAIL:
    /// one unbuildable object cost the whole module. On Microsoft's buckets that was
    /// Tests-Misc reporting 0 instead of 3,215 and Tests-Integration 0 instead of 340.
    ///
    /// Every assertion here is load-bearing in a different direction. Drop the PASS and the
    /// test passes against the old refusal; drop the SKIP and it passes against a runner that
    /// quietly discards the dropped tests; drop the exit-code check and it passes against one
    /// that has stopped reporting the loss at all.
    /// </summary>
    [SkippableFact]
    public void ExcludedTestCodeunit_NothingElseNamesIt_SurvivorsRunAndTheLostTestsAreCounted()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();

        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.Contains("Healthy_Addition_StillRuns", output, StringComparison.Ordinal);
        Assert.Contains("SKIP", output, StringComparison.Ordinal);
        Assert.Contains("Broken_NeverRuns", output, StringComparison.Ordinal);

        // The counts, not just the lines: a number that cannot be reached by discarding a test.
        Assert.Contains("Tests:         2 total", output, StringComparison.Ordinal);
        Assert.Contains("  pass:        1", output, StringComparison.Ordinal);
        Assert.Contains("  skipped:     1", output, StringComparison.Ordinal);
        Assert.Contains("partial:     1", output, StringComparison.Ordinal);

        // Still a failure. Running the survivors is not a licence to call the run clean.
        Assert.Equal(3, exit);
        Assert.Contains("did not run and are reported as SKIPPED", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #3476, the trap the first draft of that change fell into. Keeping the survivors
    /// makes the recovered module COMPILABLE, and a compiled module is cacheable — so the
    /// second run served the assembly from the AL-output cache, skipped Emit, and with it the
    /// exclusion branch, the suite error, the SKIPPED result and exit 3. Measured: a warm run
    /// reported `Tests: 1 total, pass: 1` and exit 0, byte-identical to a clean fixture.
    ///
    /// Before #3476 this could not happen — `sources` was cleared, so nothing was compiled and
    /// nothing was ever written. The fix withholds the cache entry for a module that is missing
    /// objects, which is what this test pins. A private --cache directory makes it a real cold
    /// then warm pair rather than a guess about the shared one's state.
    /// </summary>
    [SkippableFact]
    public void ExcludedTestCodeunit_SecondRunOffAWarmCache_StillReportsTheLoss()
    {
        TestArtifacts.SkipIfMissing();

        var cache = TestScratch.Dir("al-runner-excl-warm");
        Directory.CreateDirectory(cache);
        try
        {
            var (cold, coldExit) = RunRunnerOn(FixturePath, cacheDir: cache);
            Assert.Equal(3, coldExit);
            Assert.Contains("  skipped:     1", cold, StringComparison.Ordinal);

            var (warm, warmExit) = RunRunnerOn(FixturePath, cacheDir: cache);
            Assert.Equal(3, warmExit);
            Assert.Contains("EMIT-EXCLUDED", warm, StringComparison.Ordinal);
            Assert.Contains("  skipped:     1", warm, StringComparison.Ordinal);
            Assert.Contains("  pass:        1", warm, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Issue #3476, the other direction. `Codeunit.Run(60621)` binds to an Integer, so excluding
    /// the callee does not make the caller fail to compile and BC's emit-retry loop leaves the
    /// caller in the survivor set — measured: 1 excluded object, not 2. Running that module
    /// would call a codeunit that is not in it, so the module must be refused, and the refusal
    /// must say WHICH file reaches the dropped object.
    ///
    /// Note what makes this non-vacuous. "Refused" alone was already the behaviour before
    /// #3476, so the assertions that matter are the reason and the named file: they fail both
    /// against the old blanket refusal, which gave none, and against a triage that missed the
    /// id reference and ran the module anyway.
    /// </summary>
    [SkippableFact]
    public void ExcludedTestCodeunit_ReachedByASurvivorsObjectId_ModuleIsRefusedAndTheFileIsNamed()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunnerOn(Path.Combine(
            RepoRoot, "AlRunner.Tests", "Fixtures", "EmitExclusionReferencedById"));

        Assert.Equal(3, exit);
        Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);
        Assert.Contains("The module was NOT run:", output, StringComparison.Ordinal);
        Assert.Contains("name it or its object id", output, StringComparison.Ordinal);
        Assert.Contains("HealthyTests.Codeunit.al", output, StringComparison.Ordinal);

        // Refused means refused: no survivor ran, so no test result of any kind appears.
        Assert.Contains("Tests:         0 total", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ExclRef_ReachesTheDroppedCodeunitById (", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction: the healthy sibling alone must compile and pass cleanly. Without
    /// this, the test above would still pass if the runner had simply broken outright on the
    /// fixture — proving nothing about exclusion detection specifically.
    /// </summary>
    [SkippableFact]
    public void HealthyObjectsAlone_StillPass()
    {
        TestArtifacts.SkipIfMissing();

        var tmp = TestScratch.Dir("al-runner-emit-excl");
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
