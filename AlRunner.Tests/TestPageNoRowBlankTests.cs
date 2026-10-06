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

        r.AssertPassed("Codeunit73400.NoRow_Part_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.NoRow_Part_TypedReadsAreTheTypeDefault");
        r.AssertPassed("Codeunit73400.PartShowingARow_ReadsItsValues_AndBlanksOnlyWhileTheHostShowsNoLines");
        r.AssertPassed("Codeunit73400.NoRow_PartOfAHostOverAnEmptyTable_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.NoRow_ListOverAnEmptyTable_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.ListOverAnEmptyTable_TypedReadsAreTheTypeDefault");
        r.AssertPassed("Codeunit73400.NoRow_Contrast_ListWithARow_ReadsItsValues");
        r.AssertPassed("Codeunit73400.NoRow_CardOverAnEmptyTable_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.NoRow_ListOverAnEmptyTable_AfterFirstAndLast_StillReadsBlank");
        r.AssertPassed("Codeunit73400.NoRow_ListFilteredToNothing_EveryControlReadsBlank");
        r.AssertPassed("Codeunit73400.NoRow_Contrast_EditableListOverAnEmptyTable_ShowsTheDraftLine_ReadingItsDefaults");
        r.AssertPassed("Codeunit73400.NoRow_Contrast_PartUnderAnEditableHostWithNoLines_ShowsTheDraftLine_ReadingItsDefaults");
        r.AssertPassed("Codeunit73400.NoRow_Contrast_NewRowOnAnEmptyEditableList_ReadsTheValuesWrittenToIt");
        r.AssertPassed("Codeunit73400.NoRow_Action_TempListInsertAndFind_ShowsTheRow");
        r.AssertPassed("Codeunit73400.NoRow_Action_TempListInsertFindUpdate_ShowsTheRow");
        r.AssertPassed("Codeunit73400.NoRow_Action_TempListInsertOnly_ShowsTheRow");
        r.AssertPassed("Codeunit73400.NoRow_Action_TempListFieldsOnly_StaysBlank");
        r.AssertPassed("Codeunit73400.NoRow_Action_GetOfARowTheFilterHides_StaysBlank");

        r.AssertNoFailures();
    }
}
