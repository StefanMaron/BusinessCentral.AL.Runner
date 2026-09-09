// PartialCompanyInitAcceptanceEscalationTests — issue #3561, the "and nothing else" half.
//
// accept-partial-company-init suppresses ONE transition: company-init's 0 → 2. That is the whole
// claim that makes it a targeted alternative to --no-strict-exit, which forces 0 for a failing
// test, a compile failure and a lost output file as well. An implementation that suppressed more
// than the one transition — forcing 0, or clearing the accumulator before the exit code is
// computed — passes every arm in PartialCompanyInitAcceptanceTests, because nothing there earns
// an exit code of its own. These two arms are what it fails.
//
// A separate class rather than two more arms next door for a measured reason: the acceptance
// class already sums ~190 s of runner spawns, and CollectionCostOrderer schedules by COLLECTION,
// so a longer one is a longer single-threaded tail (#1887). Both are in that table.
using Xunit;
using static AlRunner.Tests.CipAcceptanceHarness;

namespace AlRunner.Tests;

public sealed class PartialCompanyInitAcceptanceEscalationTests
{
    /// <summary>
    /// Exit 1 is a statement about the AL, and the acceptance says nothing about the AL. The
    /// failing test still earns it, the accepted abort is still on every reporting surface, and
    /// the escalation line — which only ever explains a MOVED exit code — is absent because
    /// nothing moved.
    /// </summary>
    [SkippableFact]
    public void AnAcceptedAbort_WithAFailingTest_StillExitsOne()
    {
        TestArtifacts.SkipIfMissing();

        var run = RunRunner(NewCacheDir(), ManifestDir("accept-failing", AcceptEntry()),
            bundles: new[] { FailingFixturePath });

        Assert.True(run.Exit == 1,
            $"a failing test earns exit 1; accepting the abort must not lower it to 0. "
            + $"exit={run.Exit}\n{run.Output}");
        Assert.Contains("fail:        1", run.Output);
        // Accepted, therefore recorded — the acceptance is not a silencer.
        Assert.Contains("Company initialization: INCOMPLETE", run.Output);
        Assert.Contains($"[accepted: {AcceptedBecause}]", run.Output);
        Assert.DoesNotContain("[warn] company-init:", run.Output);
    }

    /// <summary>
    /// Exit 3 is the compile ladder, one rung the acceptance has no opinion about either. Two
    /// bundles, the good one FIRST so its abort is genuinely recorded in this run before the
    /// broken one fails to compile: the `[accepted:` marker is what proves that, and without it
    /// a runner that gave up at the compile failure would satisfy the exit-code assertion while
    /// never reaching the code under test.
    /// </summary>
    [SkippableFact]
    public void AnAcceptedAbort_WithACompileFailure_StillExitsThree()
    {
        TestArtifacts.SkipIfMissing();

        var run = RunRunner(NewCacheDir(), ManifestDir("accept-broken", AcceptEntry()),
            bundles: new[] { FixturePath, WriteUncompilableBundle() });

        Assert.True(run.Exit == 3,
            $"a compile failure earns exit 3; accepting the abort must not lower it to 0, nor "
            + $"let the abort raise it to 2. exit={run.Exit}\n{run.Output}");
        Assert.Contains("Company initialization: INCOMPLETE", run.Output);
        Assert.Contains($"[accepted: {AcceptedBecause}]", run.Output);
        Assert.DoesNotContain("[warn] company-init:", run.Output);
    }

    /// <summary>A bundle that reaches the compiler and does not survive it: the procedure body
    /// references an identifier nothing declares, which is a diagnostic rather than a parse
    /// error, so the object is genuinely compiled and genuinely rejected.</summary>
    private static string WriteUncompilableBundle()
    {
        var dir = TestScratch.Dir("al-runner-cipa-broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-e5f6-4708-9a1b-2c3d4e5f6013",
          "name": "Runner Tests Fixture - Company Init Partial Uncompilable",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70740, "to": 70759 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "CipBroken.Codeunit.al"), """
        codeunit 70740 "CIP Broken Tests"
        {
            Subtype = Test;

            [Test]
            procedure ThisDoesNotCompile()
            begin
                NoSuchVariable := 1;
            end;
        }
        """);
        return dir;
    }
}
