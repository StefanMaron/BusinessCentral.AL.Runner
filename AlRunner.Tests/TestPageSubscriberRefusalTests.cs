// TestPageSubscriberRefusalTests — issue #3105.
//
// This is a RUNNER-MECHANISM test. The BC claim it rests on — that `asserterror` around a
// TestPage control write traps the refusal and leaves ValidationErrorCount() = 1 — is settled
// upstream by corpus codeunits 60808, 60820 and 60836, all green on a real service tier
// (.claude/rules/bc-behavior-tests-go-upstream.md). What is pinned HERE is the one cell of that
// shape none of them reaches, and it is the cell issue #3105 reported broken:
//
//     the refusal is raised in a TABLE EVENT SUBSCRIBER that the platform's own event dispatch
//     invokes underneath Delete(true), several frames BELOW the page-global control's
//     OnValidate — not in the trigger body the way every existing test raises it.
//
// That is how Microsoft's Codeunit134614.TestRemoveSUPERPermissionsByUserAll refuses: page 9816
// "Permission Set by User"'s AllUsersHavePermission control (a page GLOBAL, not a Rec-bound
// field) deletes an "Access Control" row, and the System Application's
// "User Permissions Impl."(153).CheckSuperPermissionsBeforeDeleteAccessControl subscriber raises
// four frames down. A regression in the runner's dispatch-under-a-control-write path would leave
// 60808/60820/60836 green and break that shape silently, which is exactly the failure mode #3105
// describes.
//
// STATE OF #3105 WHEN THIS WAS WRITTEN, MEASURED — not a fix, a pin.
//
// The defect does not reproduce on main. Rebuilding the bundle #3105 names (TestAppPermissions +
// LibrarySingleServer + the two permission sets from Tests-SINGLESERVER, BC 28.1.49838.53910,
// both package caches), Codeunit134614.TestRemoveSUPERPermissionsByUserAll PASSES, and both
// assertions #3105 says are never reached run and pass. In a whole-codeunit run it still fails,
// but as the "already bound" cascade behind TestAddPermissionSet — whose root is the
// NavUserAccountHelper.IsPermissionSetAssigned NRE tracked in #3039, and whose cascade mechanism
// was settled as not-a-runner-defect in #2393. Disabling that ONE root test takes codeunit
// 134614 from 4P/11F to 13P/1F, this test among the 13.
//
// PROVING PROPERTY, demonstrated rather than asserted. Making the fixture's subscriber return
// instead of raising — the swallow #3105 hypothesised — turns the two refusal arms red with
// "NavNCLAssertErrorException: An error was expected inside an ASSERTERROR statement.", the exact
// message #3105 reports, while the accepting arm stays green. So the arms discriminate in both
// directions: an implementation that swallowed the subscriber's error fails the first two, and
// one that refused every write fails the third.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageSubscriberRefusalTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 180_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPageSubscriberRefusal");

    [Fact]
    public async Task ARefusalRaisedInATableSubscriber_ReachesTheAssertErrorAndTheControlsLedger()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"every fixture test must pass. exit={r.ExitCode}\n{r}");

        // The claim: the subscriber's Error travels out of SetValue, carrying its own
        // message, and the delete it refused did not happen.
        r.AssertPassed("Codeunit70604.SubscriberRefusal_ReachesTheAssertErrorAroundSetValue");

        // The ledger, read after asserterror swallowed the exception — the half Microsoft's
        // Codeunit134614 asserts and #3105 reported as never reached.
        r.AssertPassed("Codeunit70604.SubscriberRefusal_RecordsExactlyOneValidationError");

        // The mirror, without which "throw on every SetValue" would satisfy the two above.
        r.AssertPassed("Codeunit70604.UnguardedRow_IsDeletedAndRecordsNoValidationError");

        r.AssertNoFailures();
    }
}
