// ReportMetadataDocumentColumnTests — issue #3607.
//
// A RUNNER-MECHANISM test, not a claim about what real BC does. The behavioural claim
// ("Report Metadata 2000000139 and Report Data Items 2000000203 report these values")
// is adjudicated upstream against a live BC service tier by corpus PR #299
// (codeunit 60361 "Test Report Metadata Columns"), per
// .claude/rules/bc-behavior-tests-go-upstream.md.
//
// What this pins instead is that BC's emitter DOES state each property in the document
// the runner captures, and states it in BC's own normal form rather than echoing the AL
// source text. That is the premise the fix rests on, and it is the half a corpus test
// cannot see: the corpus asserts what the virtual table answers, never where the answer
// came from. If a future BC version stopped emitting <WordMergeDataItem>, or started
// writing the AL text into <DataItemTableView>, the corpus test would keep passing
// against the AL-derived fallback and only this test would notice.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ReportMetadataDocumentColumnTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public ReportMetadataDocumentColumnTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-report-metadata-document-tests");
        Directory.CreateDirectory(_root);
        AlReportMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlReportMetadataRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// The fixture is chosen so every assertion below fails against the value the runner
    /// derived from AL source before #3607: WordMergeDataItem was never read at all,
    /// DataItemTableView was the raw source text, and the data-item id was a 1-based
    /// declaration ordinal.
    /// </summary>
    private const string FixtureAl = """
        table 90310 "RptMetaDoc Sample"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; Description; Text[50]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        report 90311 "RptMetaDoc Fixture"
        {
            UsageCategory = None;
            ProcessingOnly = false;
            WordMergeDataItem = Src;

            dataset
            {
                dataitem(Src; "RptMetaDoc Sample")
                {
                    DataItemTableView = sorting("Entry No.") order(descending);
                    column(EntryNo; "Entry No.") { }

                    dataitem(Child; "RptMetaDoc Sample")
                    {
                        DataItemLink = "Entry No." = field("Entry No.");
                        column(ChildEntryNo; "Entry No.") { }
                    }
                }
            }
        }
        """;

    private System.Xml.XmlElement EmitAndReadDocument()
    {
        File.WriteAllText(Path.Combine(_root, "RptMetaDoc.al"), FixtureAl);

        var output = new BcCompiler().Emit(new[] { _root }, "RptMetaDocModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the report to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");

        Assert.True(AlReportMetadataRegistry.TryGet(90311, out var xml),
            "Expected report 90311's metadata document to be captured at emit time — "
            + "without it the virtual tables have nothing to read and fall back to AL source text.");
        Assert.False(string.IsNullOrEmpty(xml), "the captured document is empty");

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        Assert.NotNull(doc.DocumentElement);
        return doc.DocumentElement!;
    }

    private static string Text(System.Xml.XmlElement parent, string name)
    {
        foreach (System.Xml.XmlNode n in parent.ChildNodes)
            if (n is System.Xml.XmlElement e && e.Name == name)
                return e.InnerText ?? string.Empty;
        return string.Empty;
    }

    private static List<System.Xml.XmlElement> DataItems(System.Xml.XmlElement parent)
    {
        var found = new List<System.Xml.XmlElement>();
        foreach (System.Xml.XmlNode n in parent.ChildNodes)
            if (n is System.Xml.XmlElement e && e.Name == "DataItem")
            {
                found.Add(e);
                found.AddRange(DataItems(e));
            }
        return found;
    }

    [SkippableFact]
    public void EmittedDocument_StatesTheReportPropertiesTheVirtualTableReports()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var root = EmitAndReadDocument();

        // Positive, and the one the AL-derived path could not answer at all: the report
        // declares WordMergeDataItem, and BC states it. The virtual table hardcoded "".
        Assert.Equal("Src", Text(root, "WordMergeDataItem"));

        // BC writes booleans as "1"/"0" here. The fixture declares ProcessingOnly = false,
        // which is also the value NavReportSync's legacy stub path hardcodes to TRUE — so
        // this asserts the document disagrees with that hardcode rather than merely
        // restating a default.
        Assert.Equal("0", Text(root, "ProcessingOnly"));
        Assert.Equal("1", Text(root, "UseRequestPage"));

        // Negative: the document is about THIS report, not a template — the id and name
        // are the fixture's own, so a document served for the wrong report is detectable.
        Assert.Equal("90311", Text(root, "ID"));
        Assert.Equal("RptMetaDoc Fixture", Text(root, "Name"));
    }

    [SkippableFact]
    public void EmittedDocument_StatesEachDataItemsTableIdIndentAndCompiledView()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var items = DataItems(EmitAndReadDocument());
        Assert.Equal(2, items.Count);

        var rootItem = items.Single(i => Text(i, "DataItemVarName") == "Src");
        var childItem = items.Single(i => Text(i, "DataItemVarName") == "Child");

        // The table is already RESOLVED TO AN ID by BC — no name lookup needed, which is
        // what lets the virtual table answer a report whose data-item table the runner's
        // own name resolution would have dropped the whole row over.
        Assert.Equal("90310", Text(rootItem, "DataItemTable"));
        Assert.Equal("90310", Text(childItem, "DataItemTable"));

        // Nesting is stated, not counted.
        Assert.Equal("0", Text(rootItem, "DataItemIndent"));
        Assert.Equal("1", Text(childItem, "DataItemIndent"));

        // The view is BC's COMPILED form, not the AL source text. Asserting both
        // directions: the ORDER clause survives, and the raw source spelling does not
        // appear — the latter is what the runner used to hand out.
        var view = Text(rootItem, "DataItemTableView");
        Assert.Contains("ORDER(", view, StringComparison.Ordinal);
        Assert.DoesNotContain("sorting(", view, StringComparison.Ordinal);
        Assert.DoesNotContain("descending", view, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void EmittedDocument_DataItemIdsArePlatformAssigned_NotDeclarationOrdinals()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var items = DataItems(EmitAndReadDocument());
        var ids = items.Select(i => int.Parse(Text(i, "ID"))).ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());

        // The load-bearing property, and the reason the ordinal was not a harmless
        // stand-in: Ncl's ReportDataItemsDataProvider.GetReportDataItems keys, sorts and
        // range-filters Report Data Items rows on this id. A caller that reads an id from
        // one row and filters by it must select that row, which the ordinal could not do.
        Assert.DoesNotContain(1, ids);
        Assert.DoesNotContain(2, ids);
    }
}
