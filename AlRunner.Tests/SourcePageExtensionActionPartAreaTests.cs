// Issue #4871: the actions, actionrefs and parts a source-compiled pageextension adds, and the
// area its modify() sets on an action, read from the extension's own emitted delta document.
// What BC answers for a TestPage is the corpus's claim (the PR body's Corpus-PR: line).
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SourcePageExtensionActionPartAreaTests : IDisposable
{
    private const int PageId = 94871;
    private const int ExtensionId = 94872;
    private const int SecondExtensionId = 94874;

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public SourcePageExtensionActionPartAreaTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-source-pageext-action-area-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RemoveFromDict("_parsedPages", PageId);
        RemoveFromDict("_parsedPages", 94873);
        RemoveFromDict("_parsedPageExtensions", ExtensionId);
        RemoveFromDict("_parsedPageExtensions", SecondExtensionId);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    private const string FixtureAl = """
        table 94871 "SPAA Record"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        page 94873 "SPAA Part"
        {
            PageType = CardPart;
            SourceTable = "SPAA Record";
            layout { area(Content) { field(PartCode; Rec."Code") { ApplicationArea = All; } } }
        }

        page 94871 "SPAA Card"
        {
            PageType = Card;
            SourceTable = "SPAA Record";
            ApplicationArea = Basic;

            layout { area(Content) { field(CodeCtl; Rec."Code") { } } area(FactBoxes) { } }
            actions
            {
                area(Promoted) { }
                area(Processing)
                {
                    action(BaseBasicAct) { ApplicationArea = Basic; trigger OnAction() begin end; }
                    action(BaseServiceAct) { ApplicationArea = Service; trigger OnAction() begin end; }
                }
            }
        }

        pageextension 94872 "SPAA Card Ext" extends "SPAA Card"
        {
            layout
            {
                addlast(Content)
                {
                    part(ServicePart; "SPAA Part") { ApplicationArea = Service; }
                    part(HiddenPart; "SPAA Part") { ApplicationArea = Service; Visible = false; }
                    group(ExtPartGroup)
                    {
                        part(GroupedPart; "SPAA Part") { ApplicationArea = Basic; }
                    }
                }
                addlast(FactBoxes)
                {
                    systempart(ExtNotes; Notes) { ApplicationArea = Suite; }
                }
            }
            actions
            {
                addlast(Processing)
                {
                    action(ExtServiceAct) { ApplicationArea = Service; trigger OnAction() begin end; }
                    action(ExtNoneAct) { trigger OnAction() begin end; }
                    group(ExtGroup)
                    {
                        action(ExtGroupedBasicAct) { ApplicationArea = Basic; trigger OnAction() begin end; }
                    }
                }
                addlast(Promoted)
                {
                    actionref(ExtServiceRef; ExtServiceAct) { }
                    actionref(ExtBasicRef; BaseBasicAct) { }
                }
                modify(BaseServiceAct) { ApplicationArea = Suite; }
            }
        }
        """;

    private void Emit(string al)
    {
        File.WriteAllText(Path.Combine(_root, "Spaa.al"), al);
        var output = new BcCompiler().Emit(new[] { _root }, "SpaaModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        typeof(RecordPatches).GetMethod("TryParsePageFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { al });
    }

    private static int Id(string name)
    {
        Assert.True(AlObjectMetadataRegistry.TryGet("PageExtension", ExtensionId, out var ext));
        Assert.True(AlObjectMetadataRegistry.TryGet("Page", PageId, out var page));
        var m = System.Text.RegularExpressions.Regex.Match(ext + page, $"ID=\"(\\d+)\" Name=\"{name}\"");
        Assert.True(m.Success, $"{name} not in the emitted documents");
        return int.Parse(m.Groups[1].Value);
    }

    private static bool BasicSuiteSession(string? area)
        => !string.IsNullOrWhiteSpace(area)
           && area.Split(',').Any(a => a is "#Basic" or "#Suite" or "#All");

    private RecordPatches.SourcePageExtensionAreaSet EmitAndRead()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl);
        return RecordPatches.SourcePageExtensionAreas(PageId);
    }

    [SkippableFact]
    public void AddedActions_AnswerTheirOwnArea_OrNone_AndAreFoundInsideAnAddedGroup()
    {
        var actions = EmitAndRead().AddedActions.ToDictionary(a => a.Id, a => a.ApplicationArea);
        Assert.Equal(3, actions.Count);
        Assert.Equal("#Service", actions[Id("ExtServiceAct")]);
        Assert.Null(actions[Id("ExtNoneAct")]);
        Assert.Equal("#Basic", actions[Id("ExtGroupedBasicAct")]);
    }

    [SkippableFact]
    public void AddedActionRefs_CarryTheirTarget_AndNoAreaOfTheirOwn()
    {
        var refs = EmitAndRead().AddedActionRefs.ToDictionary(r => r.Id);
        Assert.Equal(2, refs.Count);
        Assert.Equal(Id("ExtServiceAct"), refs[Id("ExtServiceRef")].TargetId);
        Assert.Equal(Id("BaseBasicAct"), refs[Id("ExtBasicRef")].TargetId);
        Assert.Null(refs[Id("ExtServiceRef")].ApplicationArea);
    }

    [SkippableFact]
    public void ModifyOfAnActionsArea_IsAnActionChange_NotAControlChange()
    {
        var areas = EmitAndRead();
        Assert.Equal(new[] { Id("BaseServiceAct") }, areas.ActionAreaChanges.Keys);
        Assert.Equal("#Suite", areas.ActionAreaChanges[Id("BaseServiceAct")]);
        Assert.Empty(areas.AreaChanges);
    }

    [SkippableFact]
    public void AddedParts_AndSystemParts_AnswerTheirOwnArea_ALiterallyHiddenPartIsNotAreaTested()
    {
        var parts = EmitAndRead().AddedParts.OrderBy(p => p.Id).ToArray();
        Assert.Equal(new[] { (Id("ServicePart"), (string?)"#Service"), (Id("ExtNotes"), (string?)"#Suite"),
                (Id("GroupedPart"), (string?)"#Basic") }
            .OrderBy(p => p.Item1).ToArray(), parts);
    }

    [SkippableFact]
    public void BasicSuiteSession_RemovesTheServiceAndAreaLessActions_AndTheServicePart()
    {
        var (parts, actions) = ApplicationAreaControlRemoval.SourceExtensionPartsAndActionsToRemove(EmitAndRead(), BasicSuiteSession);
        Assert.Equal(new[] { Id("ServicePart") }, parts.ToArray());
        Assert.Equal(new[] { Id("ExtServiceAct"), Id("ExtNoneAct") }.OrderBy(i => i), actions.OrderBy(i => i));
    }

    [SkippableFact]
    public void AnActionRef_IsRemovedWithItsTarget_AndKeptWhenItsTargetIsKept()
    {
        var areas = EmitAndRead();
        var removedActions = new HashSet<int> { Id("ExtServiceAct") };
        var refs = ApplicationAreaControlRemoval.RejectedActionRefs(areas.AddedActionRefs, removedActions, BasicSuiteSession);
        Assert.Equal(new[] { Id("ExtServiceRef") }, refs.ToArray());
    }

    // #4876: the part itself, read through BC's own delta parser, not just its area.
    [SkippableFact]
    public void AddedPart_IsResolvedFromTheDelta_InsideAnAddedGroupToo_WithItsHostedPageAndOwnVisible()
    {
        EmitAndRead();
        var part = RecordPatches.SourcePageExtensionPart(PageId, Id("ServicePart"));
        Assert.NotNull(part);
        Assert.Equal(Id("ServicePart"), part!.ID);
        Assert.Equal(94873, part.PagePartID);
        Assert.Equal("#Service", part.ApplicationArea);

        var grouped = RecordPatches.SourcePageExtensionPart(PageId, Id("GroupedPart"));
        Assert.NotNull(grouped);
        Assert.Equal(94873, grouped!.PagePartID);

        var hidden = RecordPatches.SourcePageExtensionPart(PageId, Id("HiddenPart"));
        Assert.NotNull(hidden);
        Assert.Equal("false", hidden!.Visible?.ToString(), ignoreCase: true);
    }

    [SkippableFact]
    public void AControlIdNoExtensionAddsAsAPart_ResolvesToNull()
    {
        EmitAndRead();
        Assert.Null(RecordPatches.SourcePageExtensionPart(PageId, Id("ExtServiceAct")));
        Assert.Null(RecordPatches.SourcePageExtensionPart(PageId, 1));
    }

    [SkippableFact]
    public void AddedPart_WhenTheExtensionsDeltaDocumentIsMissing_Refuses()
    {
        EmitAndRead();
        var partId = Id("ServicePart");
        AlObjectMetadataRegistry.Clear();

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.SourcePageExtensionPart(PageId, partId));
        Assert.Contains($"pageextension {ExtensionId}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delta", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TwoExtensionsModifyingOneActionToDifferentAreas_Refuse()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl + """

            pageextension 94874 "SPAA Card Ext 2" extends "SPAA Card"
            {
                actions { modify(BaseServiceAct) { ApplicationArea = Jobs; } }
            }
            """);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.SourcePageExtensionAreas(PageId));
        Assert.Contains("action", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'#Suite'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'#Jobs'", ex.Message, StringComparison.Ordinal);
    }
}
