// TestPageNoRowBlankTests — issue #5358.
//
// A RUNNER-MECHANISM test over Fixtures/TestPageNoRowBlank, whose AL arms mirror corpus codeunit
// 69940 "BNR Blank Part Tests" (StefanMaron/BusinessCentral.AL.Language.Tests), which measures the
// same readings on real BC: a control of a part or page that shows no row reads blank whatever its
// field type, and its typed accessors answer the type's default.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageNoRowBlankTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPageNoRowBlank");

    [Fact]
    public async Task APartOrPageShowingNoRow_ReadsBlank_ForEveryFieldType()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"every fixture test must pass. exit={r.ExitCode}\n{r}");

        r.AssertPassed("Codeunit73400.PartShowingNoRow_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.PartShowingNoRow_TypedReadsAreTheTypeDefault");
        r.AssertPassed("Codeunit73400.PartShowingARow_ReadsItsValues_AndBlanksOnlyWhileTheHostShowsNoLines");
        r.AssertPassed("Codeunit73400.PartOfAHostOverAnEmptyTable_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.ListOverAnEmptyTable_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.ListOverAnEmptyTable_TypedReadsAreTheTypeDefault");
        r.AssertPassed("Codeunit73400.ListWithARow_ReadsItsValues");
        r.AssertPassed("Codeunit73400.CardOverAnEmptyTable_EveryControlReadsBlank");

        r.AssertNoFailures();
    }
}
