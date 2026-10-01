// Issue #5139: a source-compiled pageextension's modify() of a control's Editable / Visible /
// Enabled, or an action's Enabled / Visible, read from the extension's own emitted delta document
// (the host's MasterPage carries no extension delta). What BC answers is the corpus's claim,
// codeunits 68620 and 68621 (the PR body's Corpus-PR:); these pin the runner's own lookup.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SourceExtensionModifyBooleanPropertyTests : IDisposable
{
    private const int PageId = 95139;
    private const int OtherPageId = 95140;
    private const int PageExtensionId = 95141;
    private const int SecondPageExtensionId = 95142;

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public SourceExtensionModifyBooleanPropertyTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-source-ext-modify-boolean-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RemoveFromDict("_parsedPages", PageId);
        RemoveFromDict("_parsedPages", OtherPageId);
        RemoveFromDict("_parsedPageExtensions", PageExtensionId);
        RemoveFromDict("_parsedPageExtensions", SecondPageExtensionId);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    private const string FixtureAl = """
        table 95139 "SEMB Record"
        {
            fields { field(1; "Code"; Code[20]) { } field(2; Name; Text[30]) { } field(3; Flag; Boolean) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        page 95139 "SEMB Card"
        {
            PageType = Card;
            SourceTable = "SEMB Record";
            layout
            {
                area(Content)
                {
                    field(NameCtl; Rec.Name) { }
                    field(FlagCtl; Rec.Flag) { Editable = HostEditable; }
                    field(PlainCtl; Rec."Code") { }
                    field(ClashCtl; Rec."Code") { }
                    field(AgreeCtl; Rec."Code") { }
                }
            }
            actions
            {
                area(Processing)
                {
                    action(DoIt) { trigger OnAction() begin end; }
                    action(Untouched) { trigger OnAction() begin end; }
                }
            }
            var
                HostEditable: Boolean;
        }

        page 95140 "SEMB Other"
        {
            PageType = Card;
            SourceTable = "SEMB Record";
            layout { area(Content) { field(OtherCtl; Rec."Code") { } } }
        }

        pageextension 95141 "SEMB Card Ext" extends "SEMB Card"
        {
            layout
            {
                modify(NameCtl) { Editable = not ExtLocked; }
                modify(FlagCtl) { Editable = false; Visible = ExtShown; Enabled = ExtShown; }
                modify(ClashCtl) { Editable = false; }
                modify(AgreeCtl) { Editable = false; }
            }
            actions
            {
                modify(DoIt) { Enabled = not ExtLocked; Visible = false; }
            }
            var
                ExtLocked: Boolean;
                ExtShown: Boolean;
        }

        pageextension 95142 "SEMB Card Ext 2" extends "SEMB Card"
        {
            layout
            {
                modify(ClashCtl) { Editable = true; }
                modify(AgreeCtl) { Editable = false; }
            }
        }
        """;

    private void Emit()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        File.WriteAllText(Path.Combine(_root, "Semb.al"), FixtureAl);
        var output = new BcCompiler().Emit(new[] { _root }, "SembModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        typeof(RecordPatches).GetMethod("TryParsePageFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { FixtureAl });
    }

    private static int Id(string name)
    {
        Assert.True(AlObjectMetadataRegistry.TryGet("Page", PageId, out var xml));
        var m = System.Text.RegularExpressions.Regex.Match(xml!, $"ID=\"(\\d+)\" Name=\"{name}\"");
        Assert.True(m.Success, $"{name} not in the emitted page {PageId} document");
        return int.Parse(m.Groups[1].Value);
    }

    private static string? Control(string name, string property)
        => RecordPatches.SourcePageExtensionModifiedProperty(PageId, Id(name), property, isAction: false);

    private static string? Action(string name, string property)
        => RecordPatches.SourcePageExtensionModifiedProperty(PageId, Id(name), property, isAction: true);

    // The emitted spelling of an extension global is px<ext>px<ext><Name>; RunnerPageInstance's
    // EvaluateProperty resolves it against the form's registered expressions like any other.
    [SkippableFact]
    public void AControlsModifiedEditable_AnswersTheExtensionsEmittedExpression()
    {
        Emit();
        Assert.Equal($"not px{PageExtensionId}px{PageExtensionId}ExtLocked", Control("NameCtl", "Editable"));
    }

    [SkippableFact]
    public void AControlsModify_ReplacesADeclaredEditable_AndCarriesVisibleAndEnabled()
    {
        Emit();
        Assert.Equal("false", Control("FlagCtl", "Editable"));
        Assert.Equal($"px{PageExtensionId}px{PageExtensionId}ExtShown", Control("FlagCtl", "Visible"));
        Assert.Equal($"px{PageExtensionId}px{PageExtensionId}ExtShown", Control("FlagCtl", "Enabled"));
    }

    [SkippableFact]
    public void AnActionsModify_CarriesEnabledAndVisible_AndIsNotReadAsAControl()
    {
        Emit();
        Assert.Equal($"not px{PageExtensionId}px{PageExtensionId}ExtLocked", Action("DoIt", "Enabled"));
        Assert.Equal("false", Action("DoIt", "Visible"));
        Assert.Null(Control("DoIt", "Enabled"));
        Assert.Null(Action("NameCtl", "Editable"));
    }

    [SkippableFact]
    public void AnElementNoExtensionModifies_APropertyNotStated_OrAnotherPage_AnswersNull()
    {
        Emit();
        Assert.Null(Control("PlainCtl", "Editable"));
        Assert.Null(Control("NameCtl", "Visible"));
        Assert.Null(Action("Untouched", "Enabled"));
        Assert.Null(RecordPatches.SourcePageExtensionModifiedProperty(OtherPageId, Id("NameCtl"), "Editable", isAction: false));
    }

    // Two extensions whose apps are unknown: differing values refuse, as for Caption (#4928).
    [SkippableFact]
    public void TwoExtensionsModifyingDifferently_Refuse_AndAgreeingAnswer()
    {
        Emit();
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Control("ClashCtl", "Editable"));
        Assert.Contains($"pageextensions {PageExtensionId} and {SecondPageExtensionId} both modify it", ex.Message, StringComparison.Ordinal);
        Assert.Equal("false", Control("AgreeCtl", "Editable"));
    }

    [SkippableFact]
    public void AnExtensionWhoseDeltaDocumentIsMissing_Refuses()
    {
        Emit();
        var nameCtl = Id("NameCtl");
        AlObjectMetadataRegistry.Clear();
        var ex = Assert.Throws<RunnerOutOfScopeException>(() =>
            RecordPatches.SourcePageExtensionModifiedProperty(PageId, nameCtl, "Editable", isAction: false));
        Assert.Contains($"pageextension {PageExtensionId}", ex.Message, StringComparison.Ordinal);
    }
}
