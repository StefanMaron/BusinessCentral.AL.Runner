// ExpectationManifestWiringTests — tests/expectations must actually reach the run.
//
// Issue #1734: AlRunner/Infrastructure/ExpectationManifest.cs implemented the whole
// classification table in docs/expectations.md and NOTHING ever called it. Every
// expectation entry was inert: an expect-oos test still failed the run, drift in either
// direction could never fire, and the documented escape hatch for corpus tests the
// runner cannot support did not exist.
//
// These tests spawn the real CLI against Fixtures/ExpectationsBundle (one codeunit,
// one method per classification path) plus Fixtures/ExpectationsManifest and pin the
// contract end-to-end:
//   - the reclassifying paths (pass-oos / pass-known-gap / pass-divergence / skipped)
//     reach the exit code,
//   - every drift direction fails the run with the documented diagnostics,
//   - a malformed manifest aborts startup loudly,
//   - without a manifest, behaviour is unchanged.
//
// Issue #1743 widened expect-oos to also recognise the Cecil-injected
// `out-of-scope: <api> — <reason>` message convention, and #1741 added
// expect-divergence. Both are covered here end-to-end; the negatives that keep the
// widened matcher honest live in ManifestDrift_EveryDirection_FailsTheRunLoudly.
//
// #1984 added AutoProbe_ResolvesRelativeToBundlePath_EvenWhenCwdHasNoManifest and
// AutoProbe_NoManifestAnywhere_EmitsLoudDiagnostic below: the auto-probe used to
// check ONLY Environment.CurrentDirectory, so the SAME bundle silently lost its
// out-of-scope/known-gap/divergence classification depending on which directory the
// shell happened to be sitting in when al-runner was invoked. That also forced
// NoManifest_UnchangedBehaviour_OosIsAPlainFail to move its fixture bundle OUTSIDE
// this repo's working tree: this repo's own tests/expectations/ is a genuine
// ancestor of AlRunner.Tests/Fixtures/ExpectationsBundle/suite, so once the
// auto-probe walks up from the bundle path, that fixture is no longer a valid
// "no manifest reachable at all" scenario in-place.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
// [Collection("server-serial")] and no longer are — #1809.
public sealed class ExpectationManifestWiringTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string SuitePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "ExpectationsBundle", "suite");
    private static readonly string ManifestDir = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "ExpectationsManifest");
    private static readonly string MalformedManifestDir = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "ExpectationsManifestMalformed");

    // #1984: scratch root for tests that need fixture copies living OUTSIDE this
    // repo's working tree, so no ancestor of the copy accidentally carries this
    // repo's own tests/expectations/. Torn down in Dispose.
    private readonly string _scratchRoot = TestScratch.Dir("al-runner-expectations-wiring");

    public void Dispose()
    {
        try { Directory.Delete(_scratchRoot, recursive: true); } catch { }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }

    private static (string Output, int Exit) RunRunner(string runnerArgs, string? workingDir = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(' ').Append(runnerArgs);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot,
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

    private static void AssertCount(string output, string label, int expected)
    {
        var m = Regex.Match(output, Regex.Escape(label) + @"\s*(\d+)");
        Assert.True(m.Success, $"summary must report a '{label}' count.\n{output}");
        Assert.True(int.Parse(m.Groups[1].Value) == expected,
            $"expected {label} {expected}, got {m.Groups[1].Value}.\n{output}");
    }

    /// <summary>
    /// The reclassifying paths in one run: a plain pass, a TYPED OOS throw, a
    /// Cecil-injected message-convention OOS throw (#1743), a declared known-gap
    /// failure, a declared intended divergence (#1741), and a declared skip. All must
    /// land the run at exit 0, with each reclassified count reported DISTINCTLY (a
    /// green run that got there via quarantined tests must not read as an unqualified
    /// green), and the skip-declared body must never execute.
    /// </summary>
    [SkippableFact]
    public void DeclaredExpectations_ReclassifyToGreen_AndReachTheExitCode()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(
            $"--expectations \"{ManifestDir}\" --test GreenPath \"{SuitePath}\"");

        // The skip entry must prevent INVOCATION, not just hide the result.
        Assert.DoesNotContain("SKIP-DECLARED TEST BODY RAN", output, StringComparison.Ordinal);

        // Each reclassified bucket is reported distinctly, per docs/expectations.md.
        // pass-oos is 2: the typed throw AND the Cecil-injected message convention.
        AssertCount(output, "pass-oos:", 2);
        AssertCount(output, "pass-known-gap:", 1);
        AssertCount(output, "pass-divergence:", 1);
        AssertCount(output, "skipped:", 1);
        AssertCount(output, "  fail:", 0);

        // The whole point of #1734: the reclassification reaches the exit code.
        Assert.True(exit == 0,
            $"declared expectations must reclassify to a green run. exit={exit}\n{output}");
    }

    /// <summary>
    /// Every drift direction in one run. Three of these are the load-bearing negatives
    /// for #1743: teaching expect-oos the message convention must NOT turn it into a
    /// matcher that says yes to everything, so a wrong reason, a one-character-short
    /// reason, and a failure carrying no out-of-scope signal at all must all still
    /// fail. Plus the two #1741 divergence directions. Manifest drift is loud.
    /// </summary>
    [SkippableFact]
    public void ManifestDrift_EveryDirection_FailsTheRunLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner($"--expectations \"{ManifestDir}\" \"{SuitePath}\"");

        // Direction 1a: expect-oos entry whose test passes → remove the entry.
        Assert.Contains("runner now supports this surface", output, StringComparison.Ordinal);
        // Direction 1b: known-gap entry whose test passes → remove the entry, close the issue.
        Assert.Contains("close the linked issue", output, StringComparison.Ordinal);
        // Direction 1c: divergence entry whose test passes → remove the entry.
        Assert.Contains("no longer diverges from BC", output, StringComparison.Ordinal);
        // Direction 2: undeclared OOS throw → add an entry. Fires for the typed throw
        // AND for the Cecil-injected message-convention throw.
        Assert.Contains("Unexpected out-of-scope: HttpClient.Get", output, StringComparison.Ordinal);
        Assert.Contains("Add an expect-oos entry", output, StringComparison.Ordinal);
        // Direction 3a: declared reason does not match the thrown reason.
        Assert.Contains("Expected OOS reason 'email-smtp' but runner threw reason 'external-http'",
            output, StringComparison.Ordinal);
        // Direction 3b: near-miss reason ('external-htt' is a prefix of 'external-http')
        // must not match — anchors are compared for equality, not containment.
        Assert.Contains("Expected OOS reason 'external-htt' but runner threw reason 'external-http'",
            output, StringComparison.Ordinal);
        // Direction 3c: an ordinary failure under an expect-oos entry is NOT absorbed
        // as out-of-scope just because the entry says so.
        Assert.Contains("no out-of-scope signal", output, StringComparison.Ordinal);
        // Direction 4: an OOS throw under an expect-divergence entry is the wrong mode.
        Assert.Contains("Declare it expect-oos", output, StringComparison.Ordinal);

        // The drift methods are the only failures; the green-path methods still
        // reclassify (drift must not disable classification for the rest of the run).
        AssertCount(output, "pass-oos:", 2);
        AssertCount(output, "pass-known-gap:", 1);
        AssertCount(output, "pass-divergence:", 1);
        AssertCount(output, "skipped:", 1);
        AssertCount(output, "  fail:", 9);   // every Drift_* method, and nothing else

        Assert.True(exit == 1,
            $"manifest drift must fail the run (exit 1 = test failures). exit={exit}\n{output}");
    }

    /// <summary>
    /// A malformed manifest (unknown Mode) must abort startup loudly, naming the file
    /// and the bad value — never run tests against a manifest it could not parse.
    /// </summary>
    [SkippableFact]
    public void MalformedManifest_AbortsStartupLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(
            $"--expectations \"{MalformedManifestDir}\" \"{SuitePath}\"");

        Assert.Contains("unknown Mode 'expect-magic'", output, StringComparison.Ordinal);
        Assert.True(exit == 2,
            $"a malformed manifest is a bad invocation and must exit 2 without running tests. exit={exit}\n{output}");
        // Startup aborted — nothing may have run. The loader's diagnostic quotes the
        // entry by AL object name, so probe for the CLR type name ("Codeunit60810"),
        // which only per-test run output produces.
        Assert.DoesNotContain("Codeunit60810", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction: with NO manifest reachable AT ALL (neither cwd nor any
    /// ancestor of the bundle path has a tests/expectations directory, and no
    /// --expectations flag), behaviour is unchanged — an uncaught OOS throw is a
    /// plain FAIL without any drift diagnostic. Without this, the assertions above
    /// would still hold if classification ran unconditionally and rewrote every
    /// user-facing OOS failure into manifest advice.
    ///
    /// #1984: the fixture bundle is copied OUTSIDE this repo's working tree for this
    /// test specifically — see the class-level #1984 comment for why the in-repo
    /// SuitePath no longer proves "no manifest reachable" once the auto-probe walks
    /// up from the bundle path (this repo's own tests/expectations/ IS an ancestor
    /// of SuitePath).
    /// </summary>
    [SkippableFact]
    public void NoManifest_UnchangedBehaviour_OosIsAPlainFail()
    {
        TestArtifacts.SkipIfMissing();

        var isolatedBundle = Path.Combine(_scratchRoot, "no-manifest", "suite");
        CopyDirectory(SuitePath, isolatedBundle);
        var isolatedCwd = Path.Combine(_scratchRoot, "no-manifest", "cwd");
        Directory.CreateDirectory(isolatedCwd);

        var (output, exit) = RunRunner(
            $"--test Drift_OosThrownButNoEntry \"{isolatedBundle}\"",
            workingDir: isolatedCwd);

        Assert.DoesNotContain("Add an expect-oos entry", output, StringComparison.Ordinal);
        AssertCount(output, "  fail:", 1);
        Assert.True(exit == 1, $"an uncaught OOS throw stays a failing test. exit={exit}\n{output}");
    }

    /// <summary>
    /// The reported bug (#1984): a bundle whose enclosing checkout HAS a
    /// tests/expectations manifest must have it applied regardless of cwd. Before
    /// the fix, the auto-probe checked only Environment.CurrentDirectory, so this
    /// SAME invocation — manifest and bundle both copied under one "fake-repo" tree,
    /// cwd pointed at a totally unrelated directory — silently lost classification:
    /// the declared expect-oos entry never reclassified the throw, and the run
    /// reported a plain FAIL where it should report pass-oos.
    /// </summary>
    [SkippableFact]
    public void AutoProbe_ResolvesRelativeToBundlePath_EvenWhenCwdHasNoManifest()
    {
        TestArtifacts.SkipIfMissing();

        var fakeRepo = Path.Combine(_scratchRoot, "fake-repo");
        CopyDirectory(ManifestDir, Path.Combine(fakeRepo, "tests", "expectations"));
        var bundleDir = Path.Combine(fakeRepo, "nested", "path", "to", "suite");
        CopyDirectory(SuitePath, bundleDir);
        var unrelatedCwd = Path.Combine(_scratchRoot, "unrelated-cwd");
        Directory.CreateDirectory(unrelatedCwd);

        var (output, exit) = RunRunner(
            $"--test GreenPath_OosDeclared \"{bundleDir}\"",
            workingDir: unrelatedCwd);

        AssertCount(output, "pass-oos:", 1);
        AssertCount(output, "  fail:", 0);
        Assert.True(exit == 0,
            $"the bundle-path-relative manifest must reclassify the OOS throw regardless of cwd. exit={exit}\n{output}");
    }

    /// <summary>
    /// The loud-failures half of #1984: when NO manifest is reachable via either the
    /// bundle path's ancestors or cwd, the run must say so on stderr — before the
    /// fix there was no notice at any verbosity, so a corpus run from the wrong cwd
    /// silently lost classification with nothing in the output pointing at why.
    /// </summary>
    [SkippableFact]
    public void AutoProbe_NoManifestAnywhere_EmitsLoudDiagnostic()
    {
        TestArtifacts.SkipIfMissing();

        var isolatedBundle = Path.Combine(_scratchRoot, "loud-miss", "suite");
        CopyDirectory(SuitePath, isolatedBundle);
        var isolatedCwd = Path.Combine(_scratchRoot, "loud-miss", "cwd");
        Directory.CreateDirectory(isolatedCwd);

        var (output, _) = RunRunner(
            $"--test GreenPath_OosDeclared \"{isolatedBundle}\"",
            workingDir: isolatedCwd);

        Assert.Contains("[expectations] no tests/expectations manifest found", output, StringComparison.Ordinal);
    }

    // ── #3123: an entry that matches NO test ──────────────────────────────────────
    //
    // Every drift direction above is about a test the manifest MATCHED. An entry that
    // matched nothing produced no diagnostic at all: Lookup returned null, the
    // classifier took its no-entry branch, and the run's output was byte-comparable to
    // a run with an empty manifest directory. One wrong letter untracked a declared gap.
    //
    // --expectations-require-match is the opt-in that turns that into a verdict. It has
    // to be opt-in: the expectations directory is auto-probed and shared across every
    // invocation in this repo, and an entry naming a corpus codeunit legitimately
    // matches nothing in a run over a different bundle.

    /// <summary>Writes a one-entry manifest into scratch and returns its directory.</summary>
    private string OneEntryManifest(string name, string codeunitName, string method)
    {
        var dir = Path.Combine(_scratchRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "known-gaps-fixture.json"), $$"""
        [
          {
            "codeunitId": 60810,
            "CodeunitName": "{{codeunitName}}",
            "Method": "{{method}}",
            "Mode": "expect-fail-known-gap",
            "Issue": "https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3123"
          }
        ]
        """);
        return dir;
    }

    /// <summary>
    /// The correct entry, with the audit on: the known-gap failure still reclassifies to
    /// green AND the audit reports the scope it accounted for. This is the control that
    /// keeps the two tests below from passing against an implementation that always
    /// reports an unmatched entry.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_CorrectEntry_StaysGreen_AndSaysWhatItAccountedFor()
    {
        TestArtifacts.SkipIfMissing();
        var dir = OneEntryManifest("match-ok", "Expct Fixture Tests", "GreenPath_KnownGapDeclared");

        var (output, exit) = RunRunner(
            $"--expectations \"{dir}\" --expectations-require-match "
            + $"--test GreenPath_KnownGapDeclared \"{SuitePath}\"");

        AssertCount(output, "pass-known-gap:", 1);
        Assert.Contains("all 1 entries matched a discovered test", output, StringComparison.Ordinal);
        Assert.DoesNotContain("UNMATCHED", output, StringComparison.Ordinal);
        Assert.True(exit == 0, $"a matched entry must not fail the run. exit={exit}\n{output}");
    }

    /// <summary>
    /// One letter missing from CodeunitName. Before #3123 this ran as a plain FAIL with
    /// no mention of the entry anywhere; now it must name the entry, say the id was
    /// loaded under a different name, and reach exit code 5.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_MisspelledCodeunitName_FailsTheRun_AndNamesTheCorrection()
    {
        TestArtifacts.SkipIfMissing();
        // Driven against a test that PASSES, so nothing else can move the exit code and
        // the 5 is attributable to the audit alone.
        var dir = OneEntryManifest("match-name-typo", "Expct Fixture Test", "GreenPath_PlainPass");

        var (output, exit) = RunRunner(
            $"--expectations \"{dir}\" --expectations-require-match "
            + $"--test GreenPath_PlainPass \"{SuitePath}\"");

        AssertCount(output, "  fail:", 0);
        Assert.Contains("UNMATCHED", output, StringComparison.Ordinal);
        Assert.Contains("known-gaps-fixture.json", output, StringComparison.Ordinal);
        Assert.Contains("Expct Fixture Test.GreenPath_PlainPass", output, StringComparison.Ordinal);
        Assert.Contains("object id 60810 was loaded as \"Expct Fixture Tests\"", output, StringComparison.Ordinal);
        Assert.True(exit == 5,
            $"an entry that matches no test must fail with exit 5 under --expectations-require-match. "
            + $"exit={exit}\n{output}");
    }

    /// <summary>
    /// The same hole through the other field: a misspelled Method on a codeunit that IS
    /// loaded. The diagnostic must list the methods that do exist, so the correction is
    /// in the message rather than a source-tree hunt away.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_MisspelledMethod_FailsTheRun_AndListsTheRealMethods()
    {
        TestArtifacts.SkipIfMissing();
        var dir = OneEntryManifest("match-method-typo", "Expct Fixture Tests", "GreenPath_PlainPas");

        var (output, exit) = RunRunner(
            $"--expectations \"{dir}\" --expectations-require-match "
            + $"--test GreenPath_PlainPass \"{SuitePath}\"");

        AssertCount(output, "  fail:", 0);
        Assert.Contains("declares no test method 'GreenPath_PlainPas'", output, StringComparison.Ordinal);
        Assert.Contains("GreenPath_PlainPass", output, StringComparison.Ordinal);
        Assert.True(exit == 5, $"expected exit 5, got {exit}\n{output}");
    }

    /// <summary>
    /// An unmatched entry alongside a real test failure. The audit must still SAY so —
    /// that is the whole point — but exit code 1 outranks 5, because "a test failed" is
    /// the more actionable statement and the manifest problem is already in the log.
    /// This pins the ranking rather than leaving it to be rediscovered.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_AlongsideARealTestFailure_StillReports_ButTheFailureRanksFirst()
    {
        TestArtifacts.SkipIfMissing();
        var dir = OneEntryManifest("match-and-fail", "Expct Fixture Test", "GreenPath_KnownGapDeclared");

        var (output, exit) = RunRunner(
            $"--expectations \"{dir}\" --expectations-require-match "
            + $"--test GreenPath_KnownGapDeclared \"{SuitePath}\"");

        // The entry did not match, so the known-gap reclassification never happened and
        // the test failed plainly — exactly the pre-#3123 outcome, now explained.
        AssertCount(output, "  fail:", 1);
        Assert.Contains("UNMATCHED", output, StringComparison.Ordinal);
        Assert.Contains("object id 60810 was loaded as \"Expct Fixture Tests\"", output, StringComparison.Ordinal);
        Assert.True(exit == 1, $"a real test failure outranks the audit. exit={exit}\n{output}");
    }

    /// <summary>
    /// WITHOUT the flag, the misspelled entry must behave exactly as it did before
    /// #3123 — a plain failure, no audit output, exit 1. This pins the deliberate scope
    /// choice: the audit is opt-in, so the shared auto-probed manifest cannot start
    /// failing the runner-extras leg or AlRunner.Tests' own fixture runs.
    /// </summary>
    [SkippableFact]
    public void WithoutRequireMatch_AMisspelledEntry_IsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var dir = OneEntryManifest("match-no-flag", "Expct Fixture Test", "GreenPath_KnownGapDeclared");

        var (output, exit) = RunRunner(
            $"--expectations \"{dir}\" --test GreenPath_KnownGapDeclared \"{SuitePath}\"");

        Assert.DoesNotContain("UNMATCHED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("match audit", output, StringComparison.Ordinal);
        AssertCount(output, "  fail:", 1);
        Assert.True(exit == 1, $"expected the pre-#3123 behaviour, exit 1. exit={exit}\n{output}");
    }

    // ── A resumed run's final attempt (#3168) ────────────────────────────────────
    //
    // The stand-down message for a non-final attempt promises "the final attempt audits
    // the whole run". It did not: the discovery set is filled by TestExecutor in THIS
    // process, and nothing folded in the attempts carried on the command line. An entry
    // whose test ran in an earlier attempt was reported as matching nothing, and the leg
    // exited 5 telling the reader to check CodeunitName for a typo.
    //
    // Measured on the fixture below before the fix (BC 28.1, Release):
    //   entry "Ghost Resume Tests"."RanInAnEarlierAttempt", carried, --merge-results
    //     → exit 5, "no codeunit named ... was loaded in this run"
    // and after:
    //     → exit 0, "all 1 entries matched a discovered test"
    //
    // The codeunit is deliberately one the run's OWN bundle does not contain, so the
    // test cannot pass on the final attempt's own discovery — which is what a version of
    // this test driven by --exclude-test alone would have done (TestExclusionFilter is
    // consulted per METHOD, after discovery is recorded, so an excluded codeunit is still
    // loaded and still discovered).

    /// <summary>
    /// Writes a one-attempt --merge-results carry file naming one test that ran, and
    /// returns its path. Shape mirrors Infrastructure.ResumeCarry's payload exactly.
    /// </summary>
    private string CarryFile(string name, string typeName, string displayName, string method)
    {
        var dir = Path.Combine(_scratchRoot, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "attempt-results.json");
        File.WriteAllText(path, $$"""
        [{"BucketPath":"/nonexistent/earlier-attempt/suite","Stage":"Ran","CompileErrors":[],
          "ProcessError":null,
          "Tests":[{"Codeunit":"{{typeName}}","Method":"{{method}}","Outcome":"Pass",
            "Message":null,"FullException":null,"DurationTicks":10000,"AlCallStack":null,
            "CodeunitDisplayName":"{{displayName}}","Expectation":null,"InsideTestProc":false,
            "TimedOut":false,"Diagnosis":null}],
          "EmitTicks":0,"CompileTicks":0,"RunTicks":10000,"RanGroupCount":1,"ProvisionGaps":null}]
        """);
        return path;
    }

    /// <summary>Writes a one-entry manifest for an arbitrary codeunit/method pair.</summary>
    private string OneEntryManifest(string name, int codeunitId, string codeunitName, string method)
    {
        var dir = Path.Combine(_scratchRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "known-gaps-fixture.json"), $$"""
        [
          {
            "codeunitId": {{codeunitId}},
            "CodeunitName": "{{codeunitName}}",
            "Method": "{{method}}",
            "Mode": "expect-fail-known-gap",
            "Issue": "https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3168"
          }
        ]
        """);
        return dir;
    }

    /// <summary>
    /// The final attempt of a resumed run must audit the RUN, not its own slice: an entry
    /// whose test ran in an earlier attempt matched nothing and failed the leg with exit 5.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_AnEntryMatchedOnlyByACarriedAttempt_StaysGreen()
    {
        TestArtifacts.SkipIfMissing();
        var manifest = OneEntryManifest(
            "match-resumed", 60899, "Ghost Resume Tests", "RanInAnEarlierAttempt");
        var carry = CarryFile(
            "carry-resumed", "Codeunit60899", "Ghost Resume Tests", "RanInAnEarlierAttempt");

        var (output, exit) = RunRunner(
            $"--expectations \"{manifest}\" --expectations-require-match "
            + $"--merge-results \"{carry}\" --test GreenPath_PlainPass \"{SuitePath}\"");

        // The run's own bundle passed one test and failed none, so a non-zero exit here
        // could only come from the audit.
        AssertCount(output, "  pass:", 1);
        AssertCount(output, "  fail:", 0);
        Assert.DoesNotContain("UNMATCHED", output, StringComparison.Ordinal);
        Assert.Contains("all 1 entries matched a discovered test", output, StringComparison.Ordinal);
        Assert.True(exit == 0,
            $"an entry whose test ran in an earlier resume attempt must not fail the run. "
            + $"exit={exit}\n{output}");
    }

    /// <summary>
    /// The control that keeps the test above from being a rubber stamp: a carried attempt
    /// satisfies only the methods it actually ran. A method no attempt ever ran must still
    /// reach exit 5 — and the diagnostic must not claim the codeunit "declares" no such
    /// method, which a carry file cannot know.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_AMethodNoAttemptEverRan_StillFails_EvenWithACarriedAttempt()
    {
        TestArtifacts.SkipIfMissing();
        var manifest = OneEntryManifest(
            "match-resumed-typo", 60899, "Ghost Resume Tests", "NeverRanAnywhere");
        var carry = CarryFile(
            "carry-resumed-typo", "Codeunit60899", "Ghost Resume Tests", "RanInAnEarlierAttempt");

        var (output, exit) = RunRunner(
            $"--expectations \"{manifest}\" --expectations-require-match "
            + $"--merge-results \"{carry}\" --test GreenPath_PlainPass \"{SuitePath}\"");

        Assert.Contains("UNMATCHED", output, StringComparison.Ordinal);
        Assert.Contains("reached only by an earlier resume attempt", output, StringComparison.Ordinal);
        Assert.Contains("The methods it ran: RanInAnEarlierAttempt", output, StringComparison.Ordinal);
        Assert.DoesNotContain("declares no test method", output, StringComparison.Ordinal);
        Assert.True(exit == 5, $"expected exit 5, got {exit}\n{output}");
    }

    /// <summary>
    /// A carried attempt file that was named and cannot be read leaves the discovery set
    /// knowably short by a whole attempt (#2747), so the audit must stand down rather than
    /// accuse a correct entry. The run still refuses to report as complete — exit 2 — so
    /// nothing is suppressed by standing down.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_WithALostCarriedAttempt_StandsDown_RatherThanAccusingTheEntry()
    {
        TestArtifacts.SkipIfMissing();
        var manifest = OneEntryManifest(
            "match-lost-carry", 60899, "Ghost Resume Tests", "RanInAnEarlierAttempt");
        var missing = Path.Combine(_scratchRoot, "carry-lost", "attempt-results.json");

        var (output, exit) = RunRunner(
            $"--expectations \"{manifest}\" --expectations-require-match "
            + $"--merge-results \"{missing}\" --test GreenPath_PlainPass \"{SuitePath}\"");

        Assert.DoesNotContain("UNMATCHED", output, StringComparison.Ordinal);
        Assert.Contains("match audit skipped: a carried attempt file could not be read",
            output, StringComparison.Ordinal);
        Assert.True(exit == 2,
            $"a lost carried attempt must still refuse to report the run as complete. "
            + $"exit={exit}\n{output}");
    }

    /// <summary>
    /// The flag must not be satisfiable by having nothing to check. Pointed at a
    /// directory with no entries it fails rather than reporting a vacuous match.
    /// </summary>
    [SkippableFact]
    public void RequireMatch_AgainstAnEmptyManifest_FailsRatherThanPassingVacuously()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Path.Combine(_scratchRoot, "match-empty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "known-gaps-empty.json"), "[]");

        var (output, exit) = RunRunner(
            $"--expectations \"{dir}\" --expectations-require-match "
            + $"--test GreenPath_PlainPass \"{SuitePath}\"");

        Assert.Contains("declares 0 entries", output, StringComparison.Ordinal);
        Assert.True(exit == 5, $"expected exit 5, got {exit}\n{output}");
    }
}
