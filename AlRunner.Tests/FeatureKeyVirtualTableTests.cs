// FeatureKeyVirtualTableTests — issue #2585.
//
// A RUNNER-MECHANISM test: it proves the ROUTE works — that table 2000000211 reaches BC's own
// FeatureKeyDataProvider and its rows land where AL can read them — not which features BC
// ships. Naming a specific key here would pin a BC version; which keys exist is what the
// corpus adjudicates across all eight legs.
//
// Before the fix, 2000000211 had no provider, so GetDataAccessForTableCore fell through to the
// plain in-memory temp store and every read answered zero rows. Base Application's Feature
// Management reads this table to choose between a feature's modern and legacy implementation,
// so an empty table made every feature read as unregistered and the legacy path win silently.
//
// The rows are BC's own. FeatureKey.BuildFeatureKeys() is a hardcoded static list in
// Microsoft.Dynamics.Nav.Types and the runner already loads that DLL, so rebuilding the list
// here would be a second copy that drifts — and inserting one hardcoded row to steer a single
// feature, which is what prompted the issue, is the silent fake loud-failures.md bars.
//
// MEASURED on BC 28.1.49838.53910: 14 rows, every one State = None. Recorded because the
// issue predicted CalcOnlyVisibleFlowFields would be present and AllUsers, and it is not —
// that string does not appear in Types.dll or Ncl.dll on 28.1 OR 28.4. Hence no assertion
// here names a key or a state.
//
// The last test is the one that must not regress: real BC's Modify rejects a change to a
// read-only column BY NAME (issue #2636), before any write-through happens.
using Xunit;

namespace AlRunner.Tests;

public sealed class FeatureKeyVirtualTableTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 120_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "FeatureKeyVirtualTable");

    [Fact]
    public async Task FeatureKey_RoutedToBcsOwnProvider_AllFixtureTestsPass()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected a clean run (every fixture test must pass). exit={r.ExitCode}\n{r}");

        // The route works at all — the direct RED this fixes.
        r.AssertPassed("Codeunit60821.FeatureKey_AnswersBcsOwnRows");
        // Rules out N blank rows, and proves Get reaches the same rowset FindSet walked.
        r.AssertPassed("Codeunit60821.FeatureKey_EveryRowHasANonBlankIdThatGetRoundTrips");
        // Negative: a provider answering every Get with a row would pass the above.
        r.AssertPassed("Codeunit60821.FeatureKey_GetOnAnUnknownId_ReturnsFalse");
        // The read-only contract: changing a read-only column raises BC's own error naming
        // that column, before any write-through happens (#2636).
        r.AssertPassed("Codeunit60821.FeatureKey_Modify_ChangingAReadOnlyColumn_RaisesNamingTheField");
        r.AssertNoFailures();
    }
}
