// Issue #4866: the runner's MasterPage for a page carries no pageextension delta, so BC's
// application-area pass never saw a control a source-compiled pageextension adds, nor the area
// its modify() sets. These pin the runner's reading of each extension's own emitted delta
// document and the removal set built from it. What BC answers for a TestPage is the corpus's
// claim (the PR body's Corpus-PR: line).
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SourcePageExtensionApplicationAreaTests : IDisposable
{
    private const int PageId = 94861;
    private const int ExtensionId = 94862;
    private const int SecondExtensionId = 94863;

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public SourcePageExtensionApplicationAreaTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-source-pageext-area-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RemoveFromDict("_parsedPages", PageId);
        RemoveFromDict("_parsedPageExtensions", ExtensionId);
        RemoveFromDict("_parsedPageExtensions", SecondExtensionId);
        RemoveFromDict("_parsedTables", PageId);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    // The base page states ApplicationArea at object level, which BC's compiler copies onto the
    // page's own area-less controls; the extension's area-less control must not take it.
    private const string FixtureAl = """
        table 94861 "SPEA Record"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Base Value"; Text[30]) { }
                field(3; "Ext Service"; Text[30]) { }
                field(4; "Ext None"; Text[30]) { }
                field(5; "Ext Grouped"; Text[30]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        page 94861 "SPEA Card"
        {
            PageType = Card;
            SourceTable = "SPEA Record";
            ApplicationArea = Basic;

            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field(CodeCtl; Rec."Code") { }
                        field(BaseServiceCtl; Rec."Base Value") { ApplicationArea = Service; }
                    }
                }
            }
        }

        pageextension 94862 "SPEA Card Ext" extends "SPEA Card"
        {
            layout
            {
                addlast(General)
                {
                    field(ExtServiceCtl; Rec."Ext Service") { ApplicationArea = Service; }
                    field(ExtNoneCtl; Rec."Ext None") { }
                }
                addlast(Content)
                {
                    group(ExtGroup)
                    {
                        field(ExtGroupedCtl; Rec."Ext Grouped") { ApplicationArea = Basic; }
                    }
                }
                modify(BaseServiceCtl) { ApplicationArea = Suite; }
            }
        }
        """;

    private void Emit(string al)
    {
        File.WriteAllText(Path.Combine(_root, "Spea.al"), al);
        var output = new BcCompiler().Emit(new[] { _root }, "SpeaModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        // A bare Emit does not run the AL source parser a real run does; the extension lookup
        // resolves the page through it (PageControlFieldDocumentTests.EmitExtensionFixture).
        typeof(RecordPatches).GetMethod("TryParsePageFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { al });
    }

    private static int ControlId(string name)
    {
        Assert.True(AlObjectMetadataRegistry.TryGet("PageExtension", ExtensionId, out var ext));
        Assert.True(AlObjectMetadataRegistry.TryGet("Page", PageId, out var page));
        var m = System.Text.RegularExpressions.Regex.Match(ext + page, $"ID=\"(\\d+)\" Name=\"{name}\"");
        Assert.True(m.Success, $"control {name} not in the emitted documents");
        return int.Parse(m.Groups[1].Value);
    }

    // NavSession.IsApplicationAreaEnabled for a session whose areas are "#Basic,#Suite": a blank
    // area is never enabled, otherwise any listed area matches.
    private static bool BasicSuiteSession(string? area)
        => !string.IsNullOrWhiteSpace(area)
           && area.Split(',').Any(a => a is "#Basic" or "#Suite" or "#All");

    [SkippableFact]
    public void AddedControls_AnswerTheirOwnArea_OrNone_NeverThePages_AndAreFoundInsideAnAddedGroup()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl);

        var added = RecordPatches.SourcePageExtensionAreas(PageId).AddedFieldControls
            .ToDictionary(c => c.Id, c => c.ApplicationArea);

        Assert.Equal(3, added.Count);
        Assert.Equal("#Service", added[ControlId("ExtServiceCtl")]);
        Assert.Null(added[ControlId("ExtNoneCtl")]);
        Assert.Equal("#Basic", added[ControlId("ExtGroupedCtl")]);
    }

    [SkippableFact]
    public void ModifyOfApplicationArea_IsReadAsAChangeToTheBaseControl()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl);

        var changes = RecordPatches.SourcePageExtensionAreas(PageId).AreaChanges;
        Assert.Equal(new[] { ControlId("BaseServiceCtl") }, changes.Keys);
        Assert.Equal("#Suite", changes[ControlId("BaseServiceCtl")]);
    }

    [SkippableFact]
    public void BasicSuiteSession_RemovesTheServiceAndAreaLessExtensionControls_KeepsTheBasicOne()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl);

        var removed = ApplicationAreaControlRemoval.SourceExtensionFieldControlsToRemove(
                RecordPatches.SourcePageExtensionAreas(PageId), BasicSuiteSession)
            .OrderBy(id => id).ToArray();

        Assert.Equal(new[] { ControlId("ExtServiceCtl"), ControlId("ExtNoneCtl") }.OrderBy(id => id).ToArray(), removed);
    }

    [SkippableFact]
    public void TwoExtensionsModifyingOneControlToDifferentAreas_Refuse()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl + """

            pageextension 94863 "SPEA Card Ext 2" extends "SPEA Card"
            {
                layout { modify(BaseServiceCtl) { ApplicationArea = Jobs; } }
            }
            """);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.SourcePageExtensionAreas(PageId));
        Assert.Contains("'#Suite'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'#Jobs'", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void PageWithNoSourceExtension_ContributesNothing()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Emit(FixtureAl);

        var none = RecordPatches.SourcePageExtensionAreas(94869);
        Assert.Empty(none.AddedFieldControls);
        Assert.Empty(none.AreaChanges);
    }
}
