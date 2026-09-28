// Issue #4928: a source-compiled pageextension's or reportextension's modify() of a control's
// Caption and OptionCaption, read from the extension's own emitted delta document (the host's
// metadata carries no extension delta). What BC answers is the corpus's claim, codeunit 67670
// (the PR body's Corpus-PR:); these pin the runner's own lookup.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SourceExtensionModifyCaptionTests : IDisposable
{
    private const int PageId = 94928;
    private const int OtherPageId = 94929;
    private const int PageExtensionId = 94930;
    private const int ReportId = 94931;
    private const int ReportExtensionId = 94932;

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public SourceExtensionModifyCaptionTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-source-ext-modify-caption-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RemoveFromDict("_parsedPages", PageId);
        RemoveFromDict("_parsedPages", OtherPageId);
        RemoveFromDict("_parsedPageExtensions", PageExtensionId);
        RemoveFromDict("_parsedReports", ReportId);
        RemoveFromDict("_parsedReportExtensions", ReportExtensionId);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    private const string FixtureAl = """
        table 94928 "SEMC Record"
        {
            fields { field(1; "Code"; Code[20]) { } field(2; Klass; Text[30]) { Caption = 'Severity'; } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        page 94928 "SEMC Card"
        {
            PageType = Card;
            SourceTable = "SEMC Record";
            layout
            {
                area(Content)
                {
                    field(RenCtl; Rec.Klass) { }
                    field(VarCtl; HostVar) { Caption = 'Host Var'; }
                    field(OptCtl; HostOpt) { Caption = 'Host Option'; OptionCaption = 'Alpha Cap,Beta Cap'; }
                    field(PlainCtl; HostVar) { Caption = 'Host Plain'; }
                }
            }
            var
                HostVar: Text[30];
                HostOpt: Option Alpha,Beta;
        }

        page 94929 "SEMC Other"
        {
            PageType = Card;
            SourceTable = "SEMC Record";
            layout { area(Content) { field(OtherCtl; Rec."Code") { } } }
        }

        pageextension 94930 "SEMC Card Ext" extends "SEMC Card"
        {
            layout
            {
                modify(RenCtl) { Caption = 'Modified Ren'; }
                modify(VarCtl) { Caption = 'Modified; Var'; }
                modify(OptCtl) { Caption = 'Modified Option'; OptionCaption = 'Alpha Mod,Beta Mod'; }
            }
        }

        report 94931 "SEMC Report"
        {
            ProcessingOnly = true;
            requestpage
            {
                layout
                {
                    area(Content)
                    {
                        field(ReqTextCtl; ReqText) { Caption = 'Request Text'; }
                        field(ReqOptCtl; ReqOpt) { Caption = 'Request Option'; OptionCaption = 'Small,Large'; }
                        field(ReqPlainCtl; ReqText) { Caption = 'Request Plain'; }
                    }
                }
            }
            var
                ReqText: Text[30];
                ReqOpt: Option Small,Large;
        }

        reportextension 94932 "SEMC Report Ext" extends "SEMC Report"
        {
            requestpage
            {
                layout
                {
                    modify(ReqTextCtl) { Caption = 'Ext Request Text'; }
                    modify(ReqOptCtl) { OptionCaption = 'Smaller,Larger'; }
                }
            }
        }
        """;

    private void Emit()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        File.WriteAllText(Path.Combine(_root, "Semc.al"), FixtureAl);
        var output = new BcCompiler().Emit(new[] { _root }, "SemcModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        foreach (var parser in new[] { "TryParsePageFile", "TryParseReportFile" })
            typeof(RecordPatches).GetMethod(parser,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { FixtureAl });
    }

    private static int Id(string kind, int id, string name)
    {
        Assert.True(AlObjectMetadataRegistry.TryGet(kind, id, out var xml));
        var m = System.Text.RegularExpressions.Regex.Match(xml!, $"ID=\"(\\d+)\" Name=\"{name}\"");
        Assert.True(m.Success, $"{name} not in the emitted {kind} {id} document");
        return int.Parse(m.Groups[1].Value);
    }

    private static int PageControl(string name) => Id("Page", PageId, name);
    private static int RequestControl(string name) => Id("Report", ReportId, name);

    [SkippableFact]
    public void APageExtensionsModify_ReplacesCaption_WholeAcrossASemicolon()
    {
        Emit();
        Assert.Equal("Modified Ren", RecordPatches.PageExtensionModifiedControlText(PageId, PageControl("RenCtl"), "RenCtl", "Caption"));
        Assert.Equal("Modified; Var", RecordPatches.PageExtensionModifiedControlText(PageId, PageControl("VarCtl"), "VarCtl", "Caption"));
        Assert.Equal("Modified Option", RecordPatches.PageExtensionModifiedControlText(PageId, PageControl("OptCtl"), "OptCtl", "Caption"));
    }

    [SkippableFact]
    public void APageExtensionsModify_ReplacesOptionCaption_AndOnlyWhereItStatesOne()
    {
        Emit();
        Assert.Equal("Alpha Mod,Beta Mod", RecordPatches.PageExtensionModifiedControlText(PageId, PageControl("OptCtl"), "OptCtl", "OptionCaption"));
        Assert.Null(RecordPatches.PageExtensionModifiedControlText(PageId, PageControl("VarCtl"), "VarCtl", "OptionCaption"));
    }

    [SkippableFact]
    public void AControlNoPageExtensionModifies_OrAnotherPage_AnswersNull()
    {
        Emit();
        Assert.Null(RecordPatches.PageExtensionModifiedControlText(PageId, PageControl("PlainCtl"), "PlainCtl", "Caption"));
        Assert.Null(RecordPatches.PageExtensionModifiedControlText(OtherPageId, PageControl("RenCtl"), "RenCtl", "Caption"));
    }

    [SkippableFact]
    public void AReportExtensionsModify_ReplacesRequestPageCaption_AndOptionCaption()
    {
        Emit();
        Assert.Equal("Ext Request Text", RecordPatches.ReportExtensionModifiedControlText(ReportId, RequestControl("ReqTextCtl"), "ReqTextCtl", "Caption"));
        Assert.Equal("Smaller,Larger", RecordPatches.ReportExtensionModifiedControlText(ReportId, RequestControl("ReqOptCtl"), "ReqOptCtl", "OptionCaption"));
        // A modify() stating only OptionCaption leaves the Caption to the control.
        Assert.Null(RecordPatches.ReportExtensionModifiedControlText(ReportId, RequestControl("ReqOptCtl"), "ReqOptCtl", "Caption"));
        Assert.Null(RecordPatches.ReportExtensionModifiedControlText(ReportId, RequestControl("ReqPlainCtl"), "ReqPlainCtl", "Caption"));
    }

    [SkippableFact]
    public void AnExtensionWhoseDeltaDocumentIsMissing_Refuses()
    {
        Emit();
        var pageControl = PageControl("RenCtl");
        var requestControl = RequestControl("ReqTextCtl");
        AlObjectMetadataRegistry.Clear();
        var page = Assert.Throws<RunnerOutOfScopeException>(() =>
            RecordPatches.PageExtensionModifiedControlText(PageId, pageControl, "RenCtl", "Caption"));
        Assert.Contains($"pageextension {PageExtensionId}", page.Message, StringComparison.Ordinal);
        var report = Assert.Throws<RunnerOutOfScopeException>(() =>
            RecordPatches.ReportExtensionModifiedControlText(ReportId, requestControl, "ReqTextCtl", "Caption"));
        Assert.Contains($"reportextension {ReportExtensionId}", report.Message, StringComparison.Ordinal);
    }
}
