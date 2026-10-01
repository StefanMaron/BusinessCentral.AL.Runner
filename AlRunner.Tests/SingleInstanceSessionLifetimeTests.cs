// SingleInstanceSessionLifetimeTests — issue #4781.
//
// Runner-mechanism test over Fixtures/SingleInstanceSessionLifetime. What it pins is the
// runner's own bookkeeping across a test-codeunit boundary, which only exists because the
// runner replaces its table store there:
//   - the SingleInstance instance cache is no longer dropped at the boundary;
//   - every record that instance reaches is re-pointed at the replacement store on its next
//     read (RecordPatches.RecordImplementation_LiveDataAccess, wired by Cecil into every read of
//     RecordImplementation.dataAccess), keeping its filters: held directly, in an array, as a
//     RecordRef, three codeunits down, through an interface, a variant, a List or a Dictionary,
//     and on the Get and BLOB CalcFields paths the runner reads by reflection;
//   - the per-boundary sweep of the shared-object container keeps a List/Dictionary the
//     instance holds (BcRuntime.TreeObjectsReachableFromSingleInstances);
//   - the reset after the install seed still keeps install-trigger state from the first test.
// The bundle-start reset needs two runs in one process: SingleInstanceServerResetTests.
//
// The BC claim underneath (a SingleInstance instance lives on the company scope and survives a
// TestIsolation = Codeunit boundary) was measured on the Windows reference container and an MS
// SaaS sandbox with corpus codeunit 60600's former assertion — expected 0, got 99 — recorded in
// corpus issue #213. A corpus test would need one SingleInstance fixture shared by two test
// codeunits, which the corpus policy check-singleinstance-fixture-owners.py refuses (corpus
// #261; follow-up #4797). This fixture can share one, because the runner fixes the order by
// object id (#2801).
using Xunit;

namespace AlRunner.Tests;

public sealed class SingleInstanceSessionLifetimeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "SingleInstanceSessionLifetime");

    [Fact]
    public async Task SingleInstanceState_SurvivesTheTestCodeunitBoundary_AndItsRecordsReadTheLiveStore()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected every fixture test to pass. exit={r.ExitCode}\n{r}");
        // Install-trigger state is reset before the first test, and nothing else is there.
        r.AssertPassed("Codeunit71930.FirstCodeunit_StartsClean_ThenLeavesState");
        // The boundary keeps the instance.
        r.AssertPassed("Codeunit71931.SecondCodeunit_SeesTheFirstCodeunitsSingleInstanceState");
        // ...and re-points its records at the rolled-back store without resetting them.
        r.AssertPassed("Codeunit71931.SecondCodeunit_SingleInstanceRecordsReadTheRolledBackStore");
        Assert.True(r.Total == 3 && r.Passed == 3, $"expected Tests: 3 passed 3\n{r}");
    }
}
