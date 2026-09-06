// AccessControlSuperRowSeedTests — RUNNER-MECHANISM tests for the #3176 seed.
//
// WHAT IS AND IS NOT PINNED HERE
//   That a user's SUPER status is backed by an Access Control row is plain BC behaviour, and it
//   is adjudicated upstream by a real service tier: corpus codeunit 60889
//   `SessionUserIsSuper_AndAccessControlHoldsTheRowThatSaysSo` (upstream PR #204), green on all
//   eight required BC legs. That test is this change's RED -> GREEN and this file does not
//   duplicate its claim — per .claude/rules/bc-behavior-tests-go-upstream.md a BC claim belongs
//   upstream, not restated locally where only the runner would adjudicate it.
//
//   What these pin is the runner's own MECHANISM, which no corpus test can see:
//
//     1. THE ORDERING. The seed must run AFTER the User row seed and BEFORE the install-baseline
//        capture. Both edges are load-bearing and both fail silently if broken:
//          * before the User row -> the row's "User Security ID" relates to User."User Security
//            ID", which holds no row yet;
//          * after the capture   -> the row exists for exactly as long as it takes the first
//            codeunit boundary to restore the store to the baseline, so the corpus test would
//            pass or fail depending on which test in the codeunit ran first.
//        The second is the nastier one, because a run with the seed misplaced that way is green
//        on a single-test run and red on a full one. RecordPatches.UserSystemTable.cs gives this
//        exact reasoning for the User row it seeds one statement earlier.
//
//     2. THE BUNDLE RESET IS WIRED. A per-bundle latch that is never cleared seeds the first
//        bundle of a multi-bundle run and silently skips every bundle after it — the row is then
//        absent in bundles 2..N while the latch claims it was seeded.
//
//   Both are read out of TestExecutor.cs's source. That is deliberate: the ordering is a property
//   of the call sequence, and asserting it from the source is exact and costs no runner spawn,
//   where driving it through a fixture would spawn the runner (~6-70s per invocation, see
//   .claude/rules/no-base-app-in-csharp-tests.md) to observe the same three statements.
//
//   The PhaseLog mark itself is separately required by PhaseLogIntegrationTests, which runs the
//   real runner — so "the stage actually executes" is pinned by a live run over there, and the
//   order it executes in is pinned here.
using Xunit;

namespace AlRunner.Tests;

public sealed class AccessControlSuperRowSeedTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestExecutorSource()
    {
        var path = Path.Combine(RepoRoot, "AlRunner", "TestExecutor.cs");
        Assert.True(File.Exists(path), $"TestExecutor.cs not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The seed is called at all, and exactly once. A second call site would re-run the insert
    /// against a store the first call already wrote — benign today only because the latch short
    /// -circuits it, which is not a property to depend on silently.
    /// </summary>
    [Fact]
    public void AccessControlSeed_IsCalledExactlyOnce_FromTestExecutor()
    {
        var src = TestExecutorSource();
        var calls = CountOccurrences(src, "EnsureAccessControlSuperRowSeeded()");
        Assert.True(
            calls == 1,
            $"expected exactly one call to EnsureAccessControlSuperRowSeeded() in TestExecutor.cs, found {calls}. "
            + "See AlRunner#3176.");
    }

    /// <summary>
    /// ORDERING, EDGE 1: after the User row seed. The Access Control row's "User Security ID"
    /// relates to User."User Security ID", so seeding it first writes a row whose relation target
    /// does not exist.
    /// </summary>
    [Fact]
    public void AccessControlSeed_RunsAfterTheUserRowSeed()
    {
        var src = TestExecutorSource();
        var userSeed = src.IndexOf("EnsureUserSystemTableRowSeeded()", StringComparison.Ordinal);
        var acSeed = src.IndexOf("EnsureAccessControlSuperRowSeeded()", StringComparison.Ordinal);

        Assert.True(userSeed >= 0, "EnsureUserSystemTableRowSeeded() call not found in TestExecutor.cs");
        Assert.True(acSeed >= 0, "EnsureAccessControlSuperRowSeeded() call not found in TestExecutor.cs");
        Assert.True(
            userSeed < acSeed,
            "the Access Control SUPER row must be seeded AFTER the User row: its \"User Security ID\" "
            + "relates to User.\"User Security ID\", which holds no row until the User seed has run. "
            + $"Found the User seed at offset {userSeed} and the Access Control seed at {acSeed}. "
            + "See AlRunner#3176.");
    }

    /// <summary>
    /// ORDERING, EDGE 2 — the one that fails silently. Before the baseline capture, or the row
    /// lives only until the first codeunit boundary restores the store to the baseline. A run
    /// with the seed on the wrong side of this line is GREEN on a single-test invocation and RED
    /// on a full one, which is the worst shape a regression can take.
    /// </summary>
    [Fact]
    public void AccessControlSeed_RunsBeforeTheInstallBaselineIsCaptured()
    {
        var src = TestExecutorSource();
        var acSeed = src.IndexOf("EnsureAccessControlSuperRowSeeded()", StringComparison.Ordinal);
        var capture = src.IndexOf("CaptureInstallBaseline()", StringComparison.Ordinal);

        Assert.True(acSeed >= 0, "EnsureAccessControlSuperRowSeeded() call not found in TestExecutor.cs");
        Assert.True(capture >= 0, "CaptureInstallBaseline() call not found in TestExecutor.cs");
        Assert.True(
            acSeed < capture,
            "the Access Control SUPER row must be seeded BEFORE CaptureInstallBaseline(), or it is not "
            + "part of the baseline each test is restored to and survives only until the first codeunit "
            + $"boundary. Found the seed at offset {acSeed} and the capture at {capture}. See AlRunner#3176.");
    }

    /// <summary>
    /// The per-bundle latch is cleared for each new bundle. Without this the first bundle of a
    /// multi-bundle run is seeded and every bundle after it is silently skipped, while
    /// <c>AccessControlRowSeededForThisBundle</c> still reads true.
    /// </summary>
    [Fact]
    public void AccessControlSeed_LatchIsResetForEachNewBundle()
    {
        var src = TestExecutorSource();
        Assert.True(
            src.Contains("ResetAccessControlSeedForNewBundle()", StringComparison.Ordinal),
            "TestExecutor.cs never calls ResetAccessControlSeedForNewBundle(), so the per-bundle latch is "
            + "never cleared: bundle 1 gets the SUPER row and bundles 2..N silently do not. See AlRunner#3176.");

        var reset = src.IndexOf("ResetAccessControlSeedForNewBundle()", StringComparison.Ordinal);
        var seed = src.IndexOf("EnsureAccessControlSuperRowSeeded()", StringComparison.Ordinal);
        Assert.True(
            reset < seed,
            "the latch reset must come before the seed in the per-bundle sequence, or the reset clears a "
            + $"latch the seed has just set. Reset at {reset}, seed at {seed}.");
    }

    /// <summary>
    /// The seed reports its own stage, so PhaseLogIntegrationTests can require the mark and a
    /// deleted call site fails there too rather than only here.
    /// </summary>
    [Fact]
    public void AccessControlSeed_CarriesItsOwnPhaseLogStage()
    {
        var src = TestExecutorSource();
        Assert.True(
            src.Contains("\"install-seed-access-control-row\"", StringComparison.Ordinal),
            "the Access Control seed carries no PhaseLog AppStage mark, so its cost is unattributed and "
            + "PhaseLogIntegrationTests cannot require it. See AlRunner#3176.");
    }

    /// <summary>
    /// The stated SUPER fact in PermissionSetAssignment.cs is deliberately NOT removed by this
    /// change, and this pins that. A bundle whose closure carries no Access Control metatable
    /// cannot hold the seeded row at all, and IsSuper must still answer true for it or codeunit
    /// 9002 refuses a `User.Modify` every real BC test tier allows. Deleting the fact as
    /// "now redundant" would break exactly those bundles, and nothing else would say so.
    /// </summary>
    [Fact]
    public void TheStatedSuperFact_SurvivesTheSeed_ForBundlesWithNoAccessControlTable()
    {
        var path = Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.PermissionSetAssignment.cs");
        Assert.True(File.Exists(path), $"RecordPatches.PermissionSetAssignment.cs not found at {path}");
        var src = File.ReadAllText(path);

        Assert.True(
            src.Contains("IsSkeletonSessionUser(session, userSecurityId)", StringComparison.Ordinal),
            "the stated SUPER fact was removed from IsPermissionSetAssignedCore. It is NOT made redundant by "
            + "the #3176 seed: a bundle whose closure has no Access Control metatable cannot hold the seeded "
            + "row, and IsSuper must still answer true for it. See AlRunner#3176.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
