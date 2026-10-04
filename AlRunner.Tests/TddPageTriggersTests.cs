// --tdd and the code of a page that a TestPage runs (#5309): the triggers and the platform events around them, a
// control's field triggers, an action's OnAction through Invoke, what a page's own code asks of its page through
// CurrPage, and a page opened by Page.Run. A test reaching a stub through one of them is annotated although no AL
// call in it names the page's code. One run of the TddPageTriggers fixture (a single folder) is shared by the
// tests here, each asserting the tests of its own shape against the set of stubs it must name and the ones it
// must not (a test that does not run a trigger stays without its stub).
//
// Runner-specific (the annotation of a test that reaches a --tdd stub), so it lives here, not in the
// al-language corpus: the claim is about the generator. What a TestPage runs is BC's: the order and the events
// are settled by the corpus (handlers/TestPageTriggerEvents.al, pageextensiontrigger/
// TestPageExtensionPageTriggers_Tests.al), and docs/server-mode.md#tdd names them.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddPageTriggersRun : TddRunResult
{
    public TddPageTriggersRun() : base(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddPageTriggers")) { }
}

public sealed class TddPageTriggersTests : IClassFixture<TddPageTriggersRun>
{
    private readonly TddPageTriggersRun _run;

    public TddPageTriggersTests(TddPageTriggersRun run) => _run = run;

    private static string Stub(string member) => $"PT Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static string[] Sorted(IEnumerable<string> members) =>
        members.Select(Stub).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private void AssertStubs(string test, params string[] members)
    {
        Assert.True(_run.Exit == 0, $"exit {_run.Exit}\n{_run.StdErr}");
        Assert.Equal("pass", _run.Find(test).GetProperty("status").GetString());
        Assert.Equal(Sorted(members), _run.StubsOf(test).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // What opening the page on a record starts: the page's triggers and its page extension's, the events around
    // them, and the one trigger no operation is known to start (a background task completion), which every page
    // operation counts for.
    private static readonly string[] Open =
    {
        "MInit", "MOpen", "MFind", "MNext", "MAfterGet", "MAfterGetCurr", "MNew", "MBackground",
        "MEvtOpen", "MEvtAfterGet", "MEvtAfterGetCurr", "MEvtNew", "MExtOpen", "MExtAfterGet",
    };

    // The row saved when a move, a close or a typed value leaves it: the page's insert and modify triggers.
    private static readonly string[] Save = { "MInsert", "MModify", "MEvtInsert", "MEvtModify" };

    private static readonly string[] Close = { "MQueryClose", "MClose", "MEvtClose", "MEvtQuery" };

    // Every trigger and event of the page, as Page.RunModal and a built-in action that opens a page start them.
    private static readonly string[] Every = Open.Concat(Close).Concat(Save).Concat(new[] { "MDelete", "MEvtDelete" }).ToArray();

    /// <summary>What a TestPage operation starts on its page, each against what opening alone starts.</summary>
    [SkippableFact]
    public void OpenCloseAndMove_StartTheTriggersOfThePage()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("OpenView_StartsTheOpenAndRowTriggers", Open);
        AssertStubs("OpenNew_StartsTheOpenAndNewRecordTriggers", Open);
        AssertStubs("Close_AddsTheCloseTriggersAndTheSaveOfTheRow", Open.Concat(Close).Concat(Save).ToArray());
        AssertStubs("GoToKey_AddsTheSaveOfTheRowWhenItLeaves", Open.Concat(Save).ToArray());
    }

    /// <summary>
    /// A typed value runs the control's own trigger on its page: that control's and no other's, a page extension's
    /// control too, and the row it makes dirty is saved by the page. The control a TestPage reads starts none.
    /// </summary>
    [SkippableFact]
    public void TypingIntoAControl_StartsThatControlsPageTriggerOnly()
    {
        TestArtifacts.SkipIfMissing();

        // The Qty control has a trigger of its own and one a page extension's modify block adds: both are Qty's.
        AssertStubs("SetValue_OnAFieldWithAPageTrigger_StartsThatFieldsTriggerAndTheSave", Open.Concat(Save).Concat(new[] { "MQtyValidate", "MExtModifyQty" }).ToArray());
        AssertStubs("ValueAssigned_OnAFieldWithAPageTrigger_StartsThatFieldsTrigger", Open.Concat(Save).Append("MNoteValidate").ToArray());
        AssertStubs("SetValue_OnAFieldAPageExtensionAdds_StartsTheExtensionsFieldTrigger", Open.Concat(Save).Append("MExtValidate").ToArray());
        AssertStubs("Activate_OnAControl_StartsTheRowTriggersAndTheInsert", Open.Append("MInsert").Append("MEvtInsert").ToArray());
        AssertStubs("New_OnAnOpenPage_AddsTheSaveOfTheRow", Open.Concat(Save).ToArray());
        AssertStubs("Reading_AnOpenPage_StartsNoMoreThanTheOpen", Open);
    }

    /// <summary>
    /// Drilldown and AssistEdit start their own trigger of that control and nothing else; Lookup also validates and
    /// opens a lookup page the call cannot name, so it counts for every page, and a field whose lookup is the
    /// table's starts that one.
    /// </summary>
    [SkippableFact]
    public void DrilldownAssistEditAndLookup_StartTheFieldsOwnTriggers()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("Drilldown_StartsTheFieldsDrillDownOnly", Open.Append("MQtyDrill").ToArray());
        AssertStubs("AssistEdit_StartsTheFieldsAssistEditOnly", Open.Append("MQtyAssist").ToArray());
        // Invoke on a field is any of its triggers; a probe saw it run none, so this is an over-approximation.
        AssertStubs("Invoke_OnAField_StartsEachTriggerOfThatControl", Open.Concat(new[] { "MQtyLookup", "MQtyDrill", "MQtyAssist" }).ToArray());
        foreach (var test in new[] { "Lookup_OnAFieldWithAPageLookup_StartsTheLookupAndValidates", "Lookup_OnAFieldWithOnlyATableLookup_StartsTheTablesLookup" })
        {
            var stubs = _run.StubsOf(test);
            foreach (var member in Open.Concat(Save).Concat(new[] { "MTableLookup", "MPartOpen", "MEvtQuietOpen", "MDlgClose" }))
                Assert.Contains(Stub(member), stubs);
            Assert.DoesNotContain(Stub("MQtyDrill"), stubs);
            Assert.DoesNotContain(Stub("MGo"), stubs);
        }
        Assert.Contains(Stub("MQtyLookup"), _run.StubsOf("Lookup_OnAFieldWithAPageLookup_StartsTheLookupAndValidates"));
        Assert.Contains(Stub("MQtyValidate"), _run.StubsOf("Lookup_OnAFieldWithAPageLookup_StartsTheLookupAndValidates"));
        Assert.DoesNotContain(Stub("MQtyLookup"), _run.StubsOf("Lookup_OnAFieldWithOnlyATableLookup_StartsTheTablesLookup"));
    }

    /// <summary>
    /// Invoke on an action starts that action's OnAction and no other action's, the page extension's action
    /// counts for its base page, and the call can sit in a helper the test calls.
    /// </summary>
    [SkippableFact]
    public void Invoke_StartsTheOnActionOfThatActionOnly()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("Invoke_OnAnAction_StartsThatActionsOnActionOnly", Open.Append("MGo").ToArray());
        AssertStubs("Invoke_OnAnotherAction_StartsThatActionsOnActionOnly", Open.Append("MOther").ToArray());
        AssertStubs("Invoke_OnAnActionAPageExtensionAdds_StartsItsOnAction", Open.Append("MExtAction").ToArray());
        AssertStubs("Invoke_InAHelperTheTestCalls_StartsTheOnAction", Open.Append("MGo").ToArray());
        AssertStubs("Invoke_OnAnActionOfATestPartsPage_StartsThatPagesOnAction", "MPartOpen", "MLeafOpen", "MPartGo");
    }

    /// <summary>
    /// A page's own code asks its page to save or close through CurrPage: SaveRecord and Update(true) run the
    /// page's modify trigger (the stub is reached with no AL call in the test naming it), Close its close
    /// triggers, and a Rec.Modify in an action does not.
    /// </summary>
    [SkippableFact]
    public void CurrPageInAPagesCode_StartsThePagesOwnSaveAndCloseTriggers()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("Invoke_OnAnActionThatSavesThroughCurrPage_StartsTheModifyTrigger", Open.Concat(Save).ToArray());
        AssertStubs("Invoke_OnAnActionThatUpdatesThroughCurrPage_StartsTheModifyTrigger", Open.Concat(Save).ToArray());
        AssertStubs("Invoke_OnAnActionThatClosesThroughCurrPage_StartsTheCloseTriggers", Open.Concat(Close).Concat(Save).ToArray());
        AssertStubs("Invoke_OnAnActionThatModifiesTheRecord_StartsNoPageSave", Open);
        // A page extension's code asks the page it extends; a close of a RecordRef is not a close of the page.
        AssertStubs("Invoke_OnAnActionOfAPageExtensionThatSavesThroughCurrPage_StartsTheModifyTrigger", Open.Concat(Save).ToArray());
        AssertStubs("Invoke_OnAnActionThatClosesARecordRef_StartsNoCloseTrigger", Open);
    }

    /// <summary>
    /// A page opened by the code under test (Page.RunModal of a named page, Run on a Page variable) runs every
    /// trigger of the page, whatever the handler does; the built-in actions a TestPage returns (OK on a modal page) start
    /// every trigger of that page when called, and Edit on a list page opens a page the call cannot name.
    /// </summary>
    [SkippableFact]
    public void PageRunAndBuiltInActions_StartThePagesTriggers()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("RunModalOfAPage_StartsEveryTriggerOfThePage", Every);
        AssertStubs("RunModalOnAPageVariable_StartsEveryTriggerOfThePage", Every);
        AssertStubs("Run_OfAPageWithATrap_StartsEveryTriggerOfThePage", Every);
        AssertStubs("OK_OnAModalPage_StartsTheCloseTriggers", "MDlgQuery", "MDlgClose");
        // A page named by an id, a lookup page and an Edit of a list page cannot be named: every page counts.
        foreach (var test in new[] { "Edit_OnAListPage_StartsTheTriggersOfAPageTheCallCannotName", "RunModalOfAPageNamedById_StartsEveryTriggerOfEveryPage" })
        {
            var stubs = _run.StubsOf(test);
            foreach (var member in Every.Concat(new[] { "MPartOpen", "MLeafOpen", "MEvtQuietOpen", "MDlgQuery" }))
                Assert.Contains(Stub(member), stubs);
        }
    }

    /// <summary>
    /// A part runs its page's triggers when the page that hosts it opens, and its rows' save when its rows move; a
    /// page with no code, a page that is not opened and a test with no page carry none of the other pages' stubs.
    /// </summary>
    [SkippableFact]
    public void PartsAndPagesWithNoCode_StartOnlyTheirOwnTriggers()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("OpenView_OnAParentPage_StartsThePartsOpenTriggers", "MPartOpen", "MLeafOpen");
        AssertStubs("First_OnAPart_AddsThePartsSaveOfTheRow", "MPartOpen", "MLeafOpen", "MPartModify");
        AssertStubs("QuietPage_StartsOnlyItsOwnOpenEvent", "MEvtQuietOpen");
        AssertStubs("NoPage_IsNotAnnotated");
        // Every stub is generated, and the tests naming one are counted: only the test with no page is not.
        Assert.Equal(36, _run.Tests.Count);
        Assert.Equal(35, _run.Tests.Count(t => t.TryGetProperty("generatedStubs", out _)));
    }
}
