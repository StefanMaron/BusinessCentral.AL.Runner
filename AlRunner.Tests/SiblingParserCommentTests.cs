// SiblingParserCommentTests — RED→GREEN guard for #1697.
//
// #1690 fixed comment-shadowed properties in the TABLE parser only, by blanking comments out
// of the raw text before the regexes ran. The page/report/query/xmlport/object-decl/
// object-caption parsers read raw text the same way and had the same exposure — an ordinary
// comment that happens to name a property was matched AS that property.
//
// All seven parsers now run on BC's own AL syntax tree (#1696), where a comment is trivia and
// never reaches a property value, so the comment blanker is gone entirely. These tests stay:
// they pin the OBSERVABLE behaviour (via the accessors the runtime calls), not the mechanism,
// and they are exactly what proves the tree upholds what the blanker used to.
//
// These are quieter than #1690's failure, which is what makes them worth pinning: nothing
// throws. `SourceTable` rebinds the page to a different table and `InsertAllowed` flips a
// behaviour flag, so a test can pass while asserting against metadata a comment supplied.
//
// The parsers are private statics reached by reflection (same approach as
// AlSourceParserCommentTests); the assertions use the internal accessors the runtime
// itself calls, so these pin observable behaviour rather than a private field's shape.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class SiblingParserCommentTests
{
    private static readonly Type RecordPatchesType = typeof(AlRunner.Patches.RecordPatches);

    private static void Invoke(string method, string source)
    {
        var m = RecordPatchesType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"RecordPatches.{method} not found by reflection — signature may have changed.");
        m.Invoke(null, new object[] { source });
    }

    // Drops the fixture out of the process-wide parser state so these cannot leak into
    // other tests in the same run.
    private static void Forget(string dictField, int id)
    {
        var f = RecordPatchesType.GetField(dictField, BindingFlags.NonPublic | BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d) d.Remove(id);
    }

    // ── pages ────────────────────────────────────────────────────────────────

    private const int PageId = 61931;

    [Fact]
    public void Page_SourceTableNamedInAComment_DoesNotRebindThePage()
    {
        try
        {
            Invoke("TryParsePageFile", $$"""
                page {{PageId}} "CSP Flag List"
                {
                    // Migration note: this used to be SourceTable = Customer; before the split.
                    SourceTable = "CSP Flag Table";
                    layout { area(content) { repeater(g) { field(Accept; Rec.Accept) { } } } }
                }
                """);

            // Before the fix RxPageSourceTable matched inside the comment first, so the page
            // bound to Customer — a different table entirely.
            Assert.True(AlRunner.Patches.RecordPatches.IsPageParsed(PageId));
            Assert.True(AlRunner.Patches.RecordPatches.PageDeclaresSourceTable(PageId));
            Assert.Equal("CSP Flag Table", SourceTableNameOf(PageId));
        }
        finally { Forget("_parsedPages", PageId); }
    }

    [Fact]
    public void Page_InsertAllowedNamedInAComment_DoesNotFlipTheFlag()
    {
        try
        {
            Invoke("TryParsePageFile", $$"""
                page {{PageId}} "CSP Flag List"
                {
                    // Historically we set InsertAllowed = false; here. Re-enabled in v2.
                    SourceTable = "CSP Flag Table";
                    layout { area(content) { repeater(g) { field(Accept; Rec.Accept) { } } } }
                }
                """);

            // The silent one: AL's default is true and the page declares nothing, but the
            // comment used to be read as an explicit `false`, making the page non-creatable.
            Assert.True(AlRunner.Patches.RecordPatches.GetInsertAllowedForPage(PageId),
                "a commented-out InsertAllowed must not make the page non-creatable");
        }
        finally { Forget("_parsedPages", PageId); }
    }

    [Fact]
    public void Page_CommentedOutPageDeclaration_IsNotParsedAsAPage()
    {
        const int ghostId = 61932;
        try
        {
            Invoke("TryParsePageFile", $$"""
                page {{PageId}} "CSP Flag List"
                {
                    SourceTable = "CSP Flag Table";
                    layout { area(content) { repeater(g) { field(Accept; Rec.Accept) { } } } }
                }
                // page {{ghostId}} "CSP Retired List" { SourceTable = Customer; }
                """);

            Assert.True(AlRunner.Patches.RecordPatches.IsPageParsed(PageId));
            Assert.False(AlRunner.Patches.RecordPatches.IsPageParsed(ghostId),
                "a commented-out page declaration must not become page metadata");
        }
        finally { Forget("_parsedPages", PageId); Forget("_parsedPages", ghostId); }
    }

    // Negative direction: the fix must not over-reach. `//` inside a string literal is
    // literal text — blanking it would truncate the value.
    [Fact]
    public void Page_SlashesInsideAStringLiteral_AreNotTreatedAsAComment()
    {
        try
        {
            Invoke("TryParsePageFile", $$"""
                page {{PageId}} "CSP Flag List"
                {
                    Caption = 'Ratio // Net';
                    SourceTable = "CSP Flag Table";
                    layout { area(content) { repeater(g) { field(Accept; Rec.Accept) { } } } }
                }
                """);

            // Truncating at the '//' would swallow the rest of the object, including the
            // SourceTable declaration that follows it.
            Assert.True(AlRunner.Patches.RecordPatches.PageDeclaresSourceTable(PageId));
            Assert.Equal("CSP Flag Table", SourceTableNameOf(PageId));
        }
        finally { Forget("_parsedPages", PageId); }
    }

    // ── reports ──────────────────────────────────────────────────────────────

    private const int ReportId = 61933;

    [Fact]
    public void Report_ProcessingOnlyNamedInAComment_DoesNotFlipTheFlag()
    {
        try
        {
            Invoke("TryParseReportFile", $$"""
                report {{ReportId}} "CSP Flag Report"
                {
                    // Was ProcessingOnly = true; until we added the layout.
                    dataset { dataitem(Item; "CSP Flag Table") { column(Code; "Code") { } } }
                }
                """);

            Assert.False(ProcessingOnlyOf(ReportId),
                "a commented-out ProcessingOnly must not mark the report processing-only");
        }
        finally { Forget("_parsedReports", ReportId); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? SourceTableNameOf(int pageId) => (string?)Entry("_parsedPages", pageId, "SourceTableName");

    private static bool ProcessingOnlyOf(int reportId)
        => Entry("_parsedReports", reportId, "ProcessingOnly") as bool? ?? false;

    private static object? Entry(string dictField, int id, string property)
    {
        var d = (System.Collections.IDictionary)RecordPatchesType
            .GetField(dictField, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.True(d.Contains(id), $"{dictField} has no entry for {id}");
        var entry = d[id]!;
        return entry.GetType().GetProperty(property)!.GetValue(entry);
    }
}
