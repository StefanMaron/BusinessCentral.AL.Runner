// The keys a report, a query and an xmlport trigger is registered under, and what each call on one raises (#5322): pure
// functions of TddCallGraph, asserted whole, because every fixture test runs its object first and an operation that
// raises a superset cannot be told from a precise one through a run. The mapping from a call to the object it names is
// pinned through the fixtures (TddObjectTriggersTests, TddLibObjectTriggersTests).
using Xunit;

namespace AlRunner.Tests;

public sealed class TddCallGraphObjectKeyTests
{
    private static string[] Keys(TddCallGraph.ObjectFamily family, string name, bool requestPage = false, string? control = null)
        => TddCallGraph.ObjectTriggerKeys(family, "O", name, requestPage, control).ToArray();

    /// <summary>
    /// A trigger this list knows is keyed by its own name, one it does not by the key every operation of its kind
    /// raises; a request page trigger is a page trigger of the object's name (its control triggers also by control).
    /// </summary>
    [Fact]
    public void ObjectTriggerKeys_AreTheTriggersOwnNameOrTheKeyOfItsKind()
    {
        Assert.Equal(new[] { "o|onprereport" }, Keys(TddCallGraph.ObjectFamily.Report, "OnPreReport"));
        Assert.Equal(new[] { "o|oninitreport" }, Keys(TddCallGraph.ObjectFamily.Report, "OnInitReport"));
        Assert.Equal(new[] { "o|onbeforeaftergetrecord" }, Keys(TddCallGraph.ObjectFamily.Report, "OnBeforeAfterGetRecord"));
        Assert.Equal(new[] { "o|onreportcode" }, Keys(TddCallGraph.ObjectFamily.Report, "OnSomethingNew"));
        Assert.Equal(new[] { "o|onopenpage" }, Keys(TddCallGraph.ObjectFamily.Report, "OnOpenPage", requestPage: true));
        Assert.Equal(new[] { "o|onvalidate", "o|onvalidate@opt" }, Keys(TddCallGraph.ObjectFamily.Report, "OnValidate", requestPage: true, control: "Opt"));
        Assert.Equal(new[] { "o|onpagecode" }, Keys(TddCallGraph.ObjectFamily.Report, "OnSomethingNew", requestPage: true));

        Assert.Equal(new[] { "o|onbeforeopen" }, Keys(TddCallGraph.ObjectFamily.Query, "OnBeforeOpen"));
        Assert.Equal(new[] { "o|onquerycode" }, Keys(TddCallGraph.ObjectFamily.Query, "OnSomethingNew"));

        Assert.Equal(new[] { "o|oninitxmlport" }, Keys(TddCallGraph.ObjectFamily.XmlPort, "OnInitXmlPort"));
        Assert.Equal(new[] { "o|onafterassignfield" }, Keys(TddCallGraph.ObjectFamily.XmlPort, "OnAfterAssignField"));
        Assert.Equal(new[] { "o|onprexmlitem" }, Keys(TddCallGraph.ObjectFamily.XmlPort, "OnPreXmlItem"));
        Assert.Equal(new[] { "o|onxmlportcode" }, Keys(TddCallGraph.ObjectFamily.XmlPort, "OnSomethingNew"));
    }

    private static string[] Names(TddCallGraph.ObjectFamily family, string method)
        => TddCallGraph.ObjectOperation(family, method).Names.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A report: any run starts OnInitReport, the body triggers and the request page; RunRequestPage the request page
    /// and OnInitReport; the two configuring members only OnInitReport; a member the list does not know all of them.
    /// </summary>
    [Fact]
    public void AReportOperation_RaisesWhatItRuns()
    {
        var run = TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.Report, "Run");
        Assert.True(run.RequestPage);
        Assert.False(run.ImportWrites);
        Assert.Equal(
            new[] { "OnInitReport", "OnPreReport", "OnPostReport", "OnPreDataItem", "OnAfterGetRecord", "OnPostDataItem",
                "OnBeforePreDataItem", "OnAfterPreDataItem", "OnBeforeAfterGetRecord", "OnAfterAfterGetRecord",
                "OnBeforePostDataItem", "OnAfterPostDataItem", "OnReportCode" }.OrderBy(x => x, StringComparer.Ordinal),
            Names(TddCallGraph.ObjectFamily.Report, "Run"));
        Assert.Equal(Names(TddCallGraph.ObjectFamily.Report, "Run"), Names(TddCallGraph.ObjectFamily.Report, "SaveAsPdf"));
        Assert.Equal(Names(TddCallGraph.ObjectFamily.Report, "Run"), Names(TddCallGraph.ObjectFamily.Report, "AMemberABcAddsLater"));

        var request = TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.Report, "RunRequestPage");
        Assert.True(request.RequestPage);
        Assert.Equal(new[] { "OnInitReport" }, request.Names);
        foreach (var configure in new[] { "UseRequestPage", "SetTableView" })
        {
            var op = TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.Report, configure);
            Assert.False(op.RequestPage);
            Assert.Equal(new[] { "OnInitReport" }, op.Names);
        }
    }

    /// <summary>A query: Open, SaveAs... and a member the list does not know start OnBeforeOpen; the readers start nothing.</summary>
    [Fact]
    public void AQueryOperation_RaisesOnBeforeOpenUnlessItReads()
    {
        Assert.Equal(new[] { "OnBeforeOpen", "OnQueryCode" }, Names(TddCallGraph.ObjectFamily.Query, "Open"));
        Assert.Equal(new[] { "OnBeforeOpen", "OnQueryCode" }, Names(TddCallGraph.ObjectFamily.Query, "SaveAsCsv"));
        Assert.Equal(new[] { "OnBeforeOpen", "OnQueryCode" }, Names(TddCallGraph.ObjectFamily.Query, "AMemberABcAddsLater"));
        foreach (var read in new[] { "Close", "Read", "ColumnName", "SetFilter", "TopNumberOfRows" })
            Assert.Empty(Names(TddCallGraph.ObjectFamily.Query, read));
    }

    /// <summary>
    /// An xmlport: Export the export-side triggers and no write, Import the import-side ones and the writes of its tables,
    /// Run and a member the list does not know both; the configuring members only OnInitXmlPort.
    /// </summary>
    [Fact]
    public void AnXmlPortOperation_RaisesTheTriggersOfItsDirection()
    {
        var export = Names(TddCallGraph.ObjectFamily.XmlPort, "Export");
        var import = Names(TddCallGraph.ObjectFamily.XmlPort, "Import");
        Assert.Equal(
            new[] { "OnInitXmlPort", "OnPreXmlPort", "OnPostXmlPort", "OnPreXmlItem", "OnAfterGetRecord", "OnBeforePassVariable", "OnBeforePassField", "OnXmlPortCode" }
                .OrderBy(x => x, StringComparer.Ordinal),
            export);
        Assert.Equal(
            new[] { "OnInitXmlPort", "OnPreXmlPort", "OnPostXmlPort", "OnAfterInitRecord", "OnBeforeInsertRecord", "OnAfterInsertRecord",
                "OnBeforeModifyRecord", "OnAfterModifyRecord", "OnAfterAssignVariable", "OnAfterAssignField", "OnXmlPortCode" }
                .OrderBy(x => x, StringComparer.Ordinal),
            import);
        Assert.False(TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.XmlPort, "Export").ImportWrites);
        Assert.True(TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.XmlPort, "Import").ImportWrites);

        var run = TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.XmlPort, "Run");
        Assert.True(run.ImportWrites);
        Assert.True(run.RequestPage);
        Assert.Equal(export.Union(import).OrderBy(x => x, StringComparer.Ordinal), Names(TddCallGraph.ObjectFamily.XmlPort, "Run"));
        Assert.Equal(Names(TddCallGraph.ObjectFamily.XmlPort, "Run"), Names(TddCallGraph.ObjectFamily.XmlPort, "AMemberABcAddsLater"));
        foreach (var configure in new[] { "SetDestination", "SetSource", "SetTableView" })
        {
            var op = TddCallGraph.ObjectOperation(TddCallGraph.ObjectFamily.XmlPort, configure);
            Assert.Equal(new[] { "OnInitXmlPort" }, op.Names);
            Assert.False(op.ImportWrites);
        }
    }
}
