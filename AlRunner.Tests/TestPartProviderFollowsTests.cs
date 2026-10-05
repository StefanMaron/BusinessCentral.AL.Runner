// TestPartProviderFollowsTests — issue #5177.
//
// A RUNNER-MECHANISM test over Fixtures/TestPartProviderFollows, whose AL arms mirror corpus
// codeunit 69143 "PVD Provider Move Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#547),
// which measures the same readings on real BC: a part linked through a Provider follows the
// Provider's moves without being moved itself, and a part whose link matches no row reads blank.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPartProviderFollowsTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPartProviderFollows");

    [Fact]
    public async Task ProviderLinkedPart_FollowsItsProvider_AndBlanksWhenNoRowMatches()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"every fixture test must pass. exit={r.ExitCode}\n{r}");

        r.AssertPassed("Codeunit70961.DependentFollowsTheProvider_WithoutItsOwnMove");
        r.AssertPassed("Codeunit70961.DependentFollowsTheProvider_ItsOwnCursorIsOnTheProvidersRow");
        r.AssertPassed("Codeunit70961.HostMovesToAnEmptyProvider_DependentShowsNothing");
        r.AssertPassed("Codeunit70961.PlainLinkedPart_HostMovesToARowWithNoChildren_ShowsNothing");
        r.AssertPassed("Codeunit70961.HostMove_DependentsOnAfterGetRecordCount_IsNotRaisedByTheProviderWiring");

        r.AssertNoFailures();
    }
}
