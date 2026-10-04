// --tdd and what a TestPage runs on its source table (#5301): OpenNew and New start an insert, and a field's
// SetValue runs the table's OnValidate and inserts, modifies or renames the record, so a test reaching a stub
// through one of them is annotated although no AL call in it names a table operation. One run of the
// TddTestPageTriggers fixture (a single folder) is shared by the tests here, each asserting the tests of its own
// shape; the controls only read and move.
//
// Runner-specific (the annotation of a test that reaches a --tdd stub), so it lives here, not in the
// al-language corpus: the claim is about the generator. What a TestPage runs is BC's, and is settled by the
// corpus (docs/server-mode.md#tdd names the tests).
using Xunit;

namespace AlRunner.Tests;

public sealed class TddTestPageTriggersRun : TddRunResult
{
    public TddTestPageTriggersRun() : base(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddTestPageTriggers")) { }
}

public sealed class TddTestPageTriggersTests : IClassFixture<TddTestPageTriggersRun>
{
    private readonly TddTestPageTriggersRun _run;

    public TddTestPageTriggersTests(TddTestPageTriggersRun run) => _run = run;

    private static string Stub(string member) => $"TP Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private void AssertStubs(string test, params string[] members)
    {
        Assert.True(_run.Exit == 0, $"exit {_run.Exit}\n{_run.StdErr}");
        Assert.Equal("pass", _run.Find(test).GetProperty("status").GetString());
        Assert.Equal(members.Select(Stub).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            _run.StubsOf(test).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // What a typed value into a field of TP Rec starts: every trigger of the table that a write can start,
    // the table extension's field trigger among them (a validate is the table's, not one field's).
    private static readonly string[] RecWrite =
        { "MQtyValidate", "MExtNoteValidate", "MRecInsert", "MRecModify", "MRecRename" };

    /// <summary>OpenNew and New on the page of a table with an insert trigger start it, and no other trigger.</summary>
    [SkippableFact]
    public void OpenNewAndNew_StartTheInsertOfTheSourceTable()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("OpenNew_StartsAnInsert", "MRecInsert");
        AssertStubs("New_OnAnOpenPage_StartsAnInsert", "MRecInsert");
        AssertStubs("OpenNew_OnAnotherTablesPage_StartsOnlyThatTablesInsert", "MQuietInsert");
    }

    /// <summary>
    /// A typed value runs the field's OnValidate and writes the record, so the validate, insert, modify and
    /// rename triggers of the table are all started, a delete is not. The control sits on the page, on a page
    /// extension (a field of the table, a field of its table extension), or is bound to a variable.
    /// </summary>
    [SkippableFact]
    public void SetValue_StartsTheTriggersOfTheTableTheControlIsWrittenTo()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("SetValue_StartsTheValidateInsertModifyAndRenameOfTheTable", RecWrite);
        AssertStubs("SetValue_OnAControlAPageExtensionAddsForATableField_StartsTheTablesTriggers", RecWrite);
        AssertStubs("SetValue_OnAControlBoundToATableExtensionField_StartsTheBaseTablesTriggers", RecWrite);
        AssertStubs("SetValue_OnAControlBoundToAVariable_StartsTheTriggersOfThePagesTable", RecWrite);
        AssertStubs("SetValue_InAHelperTheTestCalls_StartsTheTablesTriggers", RecWrite);
    }

    /// <summary>
    /// A value is typed into a field by SetValue, by Value with an argument and by an assignment to Value (not an
    /// invocation, so it is read from the assignment statement); Activate
    /// moves the focus, which may insert a draft row.
    /// </summary>
    [SkippableFact]
    public void ValueWithAnArgumentAndAnAssignmentToValue_TypeLikeSetValue()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("ValueWithAnArgument_StartsTheTablesTriggers", RecWrite);
        AssertStubs("ValueAssigned_StartsTheTablesTriggers", RecWrite);
        AssertStubs("Activate_OnAControl_StartsAnInsert", "MRecInsert");
    }

    /// <summary>
    /// The table is the page's: a page of another table starts that table's triggers only, and a table with
    /// subscribers and no trigger raises its database events (the delete subscriber is never reached).
    /// </summary>
    [SkippableFact]
    public void SetValue_OnAnotherTablesPage_StartsThatTablesTriggersAndEvents()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("SetValue_OnAnotherTablesPage_StartsOnlyThatTablesTriggers", "MQuietValidate", "MQuietInsert");
        AssertStubs("SetValue_OnATableWithSubscribersOnly_RaisesItsDatabaseEvents",
            "MEvtInsert", "MEvtModify", "MEvtRename", "MEvtValidate");
    }

    /// <summary>
    /// A part shows another page: its New and its controls' SetValue run the part page's table, not the parent's,
    /// and moving across its rows runs nothing.
    /// </summary>
    [SkippableFact]
    public void TestPart_StartsTheTriggersOfThePartsTable()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("New_OnAPart_StartsTheInsertOfThePartsTable", "MQuietInsert");
        AssertStubs("SetValue_OnAPartsControl_StartsThePartsTablesTriggers", "MQuietValidate", "MQuietInsert");
        AssertStubs("OpenEditAndMovesInAPart_StartNothing_IsNotAnnotated");
    }

    /// <summary>
    /// A page with no source table and a control bound to a variable writes to no table the graph can read, so
    /// the value counts for every table: the trigger and the subscribers of each, never a delete.
    /// </summary>
    [SkippableFact]
    public void SetValue_OnAPageWithNoSourceTable_CountsForEveryTable()
    {
        TestArtifacts.SkipIfMissing();

        var stubs = _run.StubsOf("SetValue_OnAPageWithNoSourceTable_CountsForEveryTable");
        foreach (var member in new[] { "MRecInsert", "MRecModify", "MQtyValidate", "MQuietInsert", "MQuietValidate", "MEvtInsert", "MEvtValidate" })
            Assert.Contains(Stub(member), stubs);
        foreach (var member in new[] { "MRecDelete", "MEvtDelete" })
            Assert.DoesNotContain(Stub(member), stubs);
    }

    /// <summary>
    /// The controls: opening a page on records, moving across them and reading a field write nothing, so a table
    /// whose triggers all call a stub is not named.
    /// </summary>
    [SkippableFact]
    public void OpeningMovingAndReading_StartNoTrigger()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("OpenEditGoToAndRead_StartNothing_IsNotAnnotated");
        AssertStubs("OpenViewAndMoves_StartNothing_IsNotAnnotated");
        AssertStubs("Quiet_IsNotAnnotated");
        // Every stub is generated, and the tests naming one are counted: the controls are not among them.
        Assert.Equal(20, _run.Tests.Count);
        Assert.Equal(16, _run.Tests.Count(t => t.TryGetProperty("generatedStubs", out _)));
    }
}
