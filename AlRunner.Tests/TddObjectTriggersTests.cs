// --tdd and the triggers of a report, a query and an xmlport (#5322): a test that reaches a stub through the code a
// Run, an Open or an Import starts is annotated although no AL call in it names that code. One run of the
// TddObjectTriggers fixture (a single folder) is shared by the tests here, each asserting the tests of its own shape
// against the set of stubs it must name and the ones it must not.
//
// Runner-specific (the annotation of a test that reaches a --tdd stub), so it lives here, not in the al-language
// corpus: the claim is about the generator. What each call runs is BC's: the corpus settles the report triggers
// (handlers/TestReportRunExecution.al, reportextensiontrigger/TestReportExtensionTriggers.al) and the xmlport
// import and export (xmlport/), and docs/server-mode.md#tdd names what each call counts as starting.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddObjectTriggersRun : TddRunResult
{
    public TddObjectTriggersRun() : base(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddObjectTriggers")) { }
}

public sealed class TddObjectTriggersTests : IClassFixture<TddObjectTriggersRun>
{
    private readonly TddObjectTriggersRun _run;

    public TddObjectTriggersTests(TddObjectTriggersRun run) => _run = run;

    private static string Stub(string member) => $"OT Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static string[] Sorted(IEnumerable<string> members) =>
        members.Select(Stub).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private void AssertPasses(string test)
    {
        Assert.True(_run.Exit == 0, $"exit {_run.Exit}\n{_run.StdErr}");
        Assert.Equal("pass", _run.Find(test).GetProperty("status").GetString());
    }

    private void AssertStubs(string test, params string[] members)
    {
        AssertPasses(test);
        Assert.Equal(Sorted(members), _run.StubsOf(test).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private void AssertIncludes(string test, IEnumerable<string> present, IEnumerable<string> absent)
    {
        AssertPasses(test);
        var stubs = _run.StubsOf(test);
        foreach (var member in present) Assert.Contains(Stub(member), stubs);
        foreach (var member in absent) Assert.DoesNotContain(Stub(member), stubs);
    }

    private static readonly string[] Init = { "MRepInit" };
    // The report's own triggers and its data items', then its report extension's.
    private static readonly string[] Body = { "MRepPre", "MRepPost", "MRepPreData", "MRepAfterGet", "MRepPostData" };
    private static readonly string[] Extension =
        { "MExtPre", "MExtPost", "MExtBeforeAfterGet", "MExtAfterAfterGet", "MExtRowPre", "MExtRowGet", "MExtRowPost" };
    private static readonly string[] RequestPage = { "MRepReqOpen", "MRepReqQuery", "MRepReqClose" };
    private static readonly string[] BodyOnly =
        Body.Where(m => m != "MRepAfterGet").Concat(Extension.Where(m => m != "MExtRowGet")).ToArray();

    /// <summary>
    /// A run of a report starts OnInitReport, its triggers and its extension's, and its request page's, whatever form
    /// the run takes: Run on a variable, Report::"X", Execute (any Report member the list does not know counts the same).
    /// The control is the other report, whose trigger none of them starts.
    /// </summary>
    [SkippableFact]
    public void ARunOfAReport_StartsItsTriggersItsExtensionsAndItsRequestPage()
    {
        TestArtifacts.SkipIfMissing();

        var all = Init.Concat(Body).Concat(Extension).Concat(RequestPage).ToArray();
        AssertStubs("Report_RunOnAVariable_StartsTheReportAndItsRequestPage", all);
        AssertStubs("Report_RunOfANamedReport_StartsTheReportAndItsRequestPage", all);
        AssertStubs("Report_Execute_StartsTheReportAndItsRequestPage", all);
        AssertStubs("Report_OtherReport_StartsOnlyItsOwnTrigger", "MOtherPre");
    }

    /// <summary>
    /// RunRequestPage opens the request page and never runs the report: its triggers and OnInitReport, none of the
    /// report's own (a request page's OnAfterGetRecord and a data item's share a key, so those two count).
    /// UseRequestPage and SetTableView are only the first use of the variable.
    /// </summary>
    [SkippableFact]
    public void RunRequestPageAndTheConfiguringMembers_StartLessThanARun()
    {
        TestArtifacts.SkipIfMissing();

        AssertIncludes("Report_RunRequestPage_StartsTheRequestPageAndNotTheBody", Init.Concat(RequestPage), BodyOnly);
        AssertStubs("Report_OnlyConfigured_StartsOnlyTheInitTrigger", Init);
        AssertStubs("Report_ThatCallsNoStub_IsNotAnnotated");
    }

    /// <summary>
    /// A request page control's trigger counts through the TestRequestPage control call in the handler that sets
    /// it, and not for a run whose handler only confirms; a report named by an id is every report.
    /// </summary>
    [SkippableFact]
    public void ARequestPageFieldAndAReportNamedById_AreFollowed()
    {
        TestArtifacts.SkipIfMissing();

        AssertIncludes("Report_HandlerSetsAField_StartsThatFieldsTrigger", new[] { "MRepReqValidate" }.Concat(Body), Array.Empty<string>());
        foreach (var test in new[] { "Report_RunOnAVariable_StartsTheReportAndItsRequestPage", "Report_RunOfANamedReport_StartsTheReportAndItsRequestPage" })
            Assert.DoesNotContain(Stub("MRepReqValidate"), _run.StubsOf(test));
        AssertIncludes("Report_RunById_StartsTheTriggersOfEveryReport", Init.Concat(Body).Concat(Extension).Concat(RequestPage).Append("MOtherPre"), Array.Empty<string>());
    }

    /// <summary>
    /// A query's Open and SaveAs... start OnBeforeOpen, that query's and no other's; a query named by an id is every
    /// query; reading its columns starts nothing.
    /// </summary>
    [SkippableFact]
    public void AQueryOpenAndSaveAs_StartOnBeforeOpenOfThatQuery()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("Query_Open_StartsOnBeforeOpen", "MQryBefore");
        AssertStubs("Query_SaveAsOfANamedQuery_StartsOnBeforeOpen", "MQryBefore");
        AssertStubs("Query_SaveAsById_StartsTheTriggersOfEveryQuery", "MQryBefore", "MOtherQryBefore");
        AssertStubs("Query_OnlyRead_StartsNoTrigger");
    }

    /// <summary>
    /// An export starts the xmlport's own triggers and its element triggers on the way out, an import the ones on the
    /// way in and writes the table of each table element (its validation, insert and modify, with the table's own
    /// triggers); the other xmlport's table is not written by it, and Set... members are the first use of the variable.
    /// </summary>
    [SkippableFact]
    public void AnXmlPortExportAndImport_StartTheirTriggersAndTheTablesWrites()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("XmlPort_Export_StartsTheExportTriggers", "MXmlInit", "MXmlPre", "MXmlPost", "MXmlRowPre", "MXmlRowGet", "MXmlBeforePassField");
        AssertStubs("XmlPort_Import_StartsTheImportTriggersAndTheTablesWrites",
            "MXmlInit", "MXmlPre", "MXmlPost", "MXmlBeforeInsert", "MXmlAfterInsert", "MXmlAfterAssignField",
            "MRecInsert", "MRecModify", "MRecValidate");
        AssertStubs("XmlPort_ImportOfAnotherXmlPort_WritesThatXmlPortsTable", "MOtherXmlPre", "MStoreInsert");
        AssertStubs("XmlPort_OnlyConfigured_StartsOnlyTheInitTrigger", "MXmlInit");
        AssertIncludes("XmlPort_StaticExportById_StartsTheTriggersOfEveryXmlPort",
            new[] { "MXmlInit", "MXmlPre", "MXmlPost", "MXmlRowPre", "MXmlRowGet", "MXmlBeforePassField", "MOtherXmlPre" },
            new[] { "MXmlBeforeInsert", "MRecInsert" });
    }

    /// <summary>A test with no report, query or xmlport carries none of their stubs, and every other test is annotated.</summary>
    [SkippableFact]
    public void ATestWithNoObject_IsNotAnnotated()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("NoObject_IsNotAnnotated");
        Assert.Equal(19, _run.Tests.Count);
        Assert.Equal(16, _run.Tests.Count(t => t.TryGetProperty("generatedStubs", out _)));
    }
}
