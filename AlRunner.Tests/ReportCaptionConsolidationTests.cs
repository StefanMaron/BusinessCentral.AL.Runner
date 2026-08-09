// ReportCaptionConsolidationTests — RED→GREEN guard for #1714.
//
// A report's Caption used to be parsed TWICE by two independent passes:
//   * AlReportParser  → ParsedReport.Caption (read by the Report Metadata virtual
//     table and, via EnumerateKnownAlObjects, by AllObjWithCaption),
//   * AlObjectCaptionParser → _parsedObjectCaptions[("Report", id)] (written on every
//     parse and read by nobody — AllObjVirtualTable's "Report" branch was the one kind
//     that bypassed SourceCaptionFor).
//
// The two agreed, but nothing enforced it, and "make AllObjVirtualTable use
// SourceCaptionFor uniformly" LOOKED like pure cleanup while actually switching which
// of two independently-computed values was authoritative for reports.
//
// The consolidation: AlReportParser is the ONLY pass that reads a report's Caption (it
// already holds the report's PropertyList for ProcessingOnly and UseRequestPage, so name
// and caption necessarily come off the same syntax node), AlObjectCaptionParser skips the
// Report kind entirely, and SourceCaptionFor("Report", id) reads through to that single
// fact so every AllObj/AllObjWithCaption kind can go through one accessor.
//
// These tests pin all three halves. The parsers are private statics reached by
// reflection (same approach as SiblingParserCommentTests); the assertions use concrete
// captions that DIFFER from the object name, so an implementation that answered with
// the name — the single most likely wrong answer here — fails.
using System.Collections;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class ReportCaptionConsolidationTests
{
    private static readonly Type RecordPatchesType = typeof(AlRunner.Patches.RecordPatches);

    // Ids picked well outside every other parser test's range so a leak is obvious.
    private const int ReportId = 61974;
    private const int NoCaptionReportId = 61975;
    private const int TableId = 61976;

    private const string ReportName = "RCC Doc Report";
    private const string ReportCaption = "RCC Document Report";

    // A report whose Caption is deliberately different from its name, a report that
    // declares no Caption at all, and a table that DOES declare one (the control: it
    // proves the object-caption parser really ran over this source, so "no Report
    // entry" is an observation and not a vacuous assertion).
    private static string Fixture() => $$"""
        table {{TableId}} "RCC Anchor"
        {
            Caption = 'RCC Anchor Caption';
            fields { field(1; "No."; Code[20]) { } }
        }

        report {{ReportId}} "{{ReportName}}"
        {
            Caption = '{{ReportCaption}}';
            dataset { dataitem(Header; "RCC Anchor") { column(No; Header."No.") { } } }
        }

        report {{NoCaptionReportId}} "RCC Silent Report"
        {
            dataset { dataitem(Header; "RCC Anchor") { column(No; Header."No.") { } } }
        }
        """;

    // ── the duplicate write is gone ──────────────────────────────────────────

    [Fact]
    public void ObjectCaptionParser_RecordsNoEntryForAReport()
    {
        try
        {
            Invoke("TryParseObjectCaptionFile", Fixture());

            // Control: the same source's TABLE caption IS recorded, so the parser
            // demonstrably ran and the Report assertions below are not vacuous.
            Assert.True(HasObjectCaption("Table", TableId),
                "the object-caption parser did not record the control table — the fixture or the parser changed");
            Assert.Equal("RCC Anchor Caption", ObjectCaption("Table", TableId));

            // The report's caption belongs to AlReportParser alone. A second, independently
            // computed copy here is exactly the duplicated fact #1714 removed.
            Assert.False(HasObjectCaption("Report", ReportId),
                "a report's Caption must be parsed by AlReportParser only — _parsedObjectCaptions must not hold a second copy");
            Assert.False(HasObjectCaption("Report", NoCaptionReportId),
                "a report's Caption must be parsed by AlReportParser only — _parsedObjectCaptions must not hold a second copy");
        }
        finally { ForgetObjectCaptions(); }
    }

    // ── the single accessor reads the single fact ────────────────────────────

    [Fact]
    public void SourceCaptionFor_Report_ReturnsTheCaptionAlReportParserRead()
    {
        try
        {
            // ONLY the report parser runs. Before the consolidation SourceCaptionFor
            // answered out of _parsedObjectCaptions and returned null here.
            Invoke("TryParseReportFile", Fixture());

            Assert.Equal(ReportCaption, SourceCaptionFor("Report", ReportId));
            // The caption differs from the object name on purpose: an implementation that
            // fell back to the name (or to string.Empty) fails right here.
            Assert.NotEqual(ReportName, SourceCaptionFor("Report", ReportId));
            // ...and it is the very same value ParsedReport carries — one fact, one place.
            Assert.Equal(ParsedReportCaption(ReportId), SourceCaptionFor("Report", ReportId));
        }
        finally { ForgetReports(); }
    }

    [Fact]
    public void SourceCaptionFor_Report_IsNullWhenTheReportDeclaresNoCaption()
    {
        try
        {
            Invoke("TryParseReportFile", Fixture());

            // "declares no Caption" must stay distinguishable from "declared as the name"
            // and from "declared empty": AL's default caption (the object name) is applied
            // by the CONSUMER — AllObjWithCaption's populate and Report Metadata's
            // `parsed.Caption ?? parsed.Name` — never invented here.
            Assert.Null(SourceCaptionFor("Report", NoCaptionReportId));
            Assert.Null(ParsedReportCaption(NoCaptionReportId));
        }
        finally { ForgetReports(); }
    }

    [Fact]
    public void SourceCaptionFor_UnknownReportId_IsNull()
    {
        try
        {
            Invoke("TryParseReportFile", Fixture());

            // Negative: no report 61999 was ever parsed, so there is nothing to report.
            // An implementation that manufactured a caption would fail here.
            Assert.Null(SourceCaptionFor("Report", 61999));
        }
        finally { ForgetReports(); }
    }

    // ── the consumer (AllObj / AllObjWithCaption row inventory) ──────────────

    [Fact]
    public void KnownAlObjects_CarryTheReportCaptionAlReportParserRead()
    {
        try
        {
            Invoke("TryParseReportFile", Fixture());

            var captioned = KnownAlObject("Report", ReportId);
            Assert.Equal(ReportName, captioned.Name);
            Assert.Equal(ReportCaption, captioned.Caption);

            // The undeclared case reaches the consumer as null, which is what lets
            // AllObjWithCaption apply AL's own default (the object name) exactly once.
            var silent = KnownAlObject("Report", NoCaptionReportId);
            Assert.Equal("RCC Silent Report", silent.Name);
            Assert.Null(silent.Caption);
        }
        finally { ForgetReports(); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void Invoke(string method, string source)
    {
        var m = RecordPatchesType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"RecordPatches.{method} not found by reflection — signature may have changed.");
        m.Invoke(null, new object[] { source });
    }

    private static string? SourceCaptionFor(string kind, int id)
    {
        var m = RecordPatchesType.GetMethod("SourceCaptionFor", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RecordPatches.SourceCaptionFor not found by reflection.");
        return (string?)m.Invoke(null, new object[] { kind, id });
    }

    private static (string Kind, int Id, string Name, string? Caption) KnownAlObject(string kind, int id)
    {
        var m = RecordPatchesType.GetMethod("EnumerateKnownAlObjects", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RecordPatches.EnumerateKnownAlObjects not found by reflection.");
        var rows = (IEnumerable<(string Kind, int Id, string Name, string? Caption)>)m.Invoke(null, null)!;
        var match = rows.Where(r => r.Kind == kind && r.Id == id).ToList();
        Assert.True(match.Count == 1, $"expected exactly one {kind} {id} row, got {match.Count}");
        return match[0];
    }

    private static IDictionary ObjectCaptions() => (IDictionary)RecordPatchesType
        .GetField("_parsedObjectCaptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static bool HasObjectCaption(string kind, int id) => ObjectCaptions().Contains((kind, id));

    private static string? ObjectCaption(string kind, int id) => (string?)ObjectCaptions()[(kind, id)];

    private static string? ParsedReportCaption(int id)
    {
        var d = (IDictionary)RecordPatchesType
            .GetField("_parsedReports", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.True(d.Contains(id), $"_parsedReports has no entry for {id}");
        var entry = d[id]!;
        return (string?)entry.GetType().GetProperty("Caption")!.GetValue(entry);
    }

    // Drop the fixtures out of the process-wide parser state so they cannot leak.
    private static void ForgetReports()
    {
        var d = (IDictionary)RecordPatchesType
            .GetField("_parsedReports", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        d.Remove(ReportId);
        d.Remove(NoCaptionReportId);
    }

    private static void ForgetObjectCaptions()
    {
        var d = ObjectCaptions();
        foreach (var kind in new[] { "Report", "Table" })
            foreach (var id in new[] { ReportId, NoCaptionReportId, TableId })
                d.Remove((kind, id));
    }
}
