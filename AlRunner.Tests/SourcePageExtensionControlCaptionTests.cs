// Issue #4913: the Caption and OptionCaption of a control a source-compiled pageextension adds,
// read from the extension's own emitted delta document (the host's MasterPage carries no delta).
// What BC answers for a TestPage is the corpus's claim, codeunit 67660 (the PR body's Corpus-PR:).
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SourcePageExtensionControlCaptionTests : IDisposable
{
    private const int PageId = 94913;
    private const int OtherPageId = 94915;
    private const int ExtensionId = 94914;

    private readonly string _root;

    public SourcePageExtensionControlCaptionTests(BcEngineFixture engine)
    {
        _ = engine;
        _root = TestScratch.Dir("al-runner-source-pageext-control-caption-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        RemoveFromDict("_parsedPages", PageId);
        RemoveFromDict("_parsedPages", OtherPageId);
        RemoveFromDict("_parsedPageExtensions", ExtensionId);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }

    private const string FixtureAl = """
        table 94913 "SPCC Record"
        {
            fields { field(1; "Code"; Code[20]) { } field(2; Klass; Text[30]) { Caption = 'Severity'; } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        page 94913 "SPCC Card"
        {
            PageType = Card;
            SourceTable = "SPCC Record";
            layout { area(Content) { field(CodeCtl; Rec."Code") { } } }
        }

        page 94915 "SPCC Other"
        {
            PageType = Card;
            SourceTable = "SPCC Record";
            layout { area(Content) { field(OtherCtl; Rec."Code") { } } }
        }

        pageextension 94914 "SPCC Card Ext" extends "SPCC Card"
        {
            layout
            {
                addlast(Content)
                {
                    field(ExtRecCtl; Rec.Klass) { Caption = 'Ext Severity'; }
                    field(ExtRecNoCapCtl; Rec.Klass) { }
                    field(ExtVarCtl; ExtVar) { Caption = 'Ext; Semi Caption'; }
                    field(ExtNoCapCtl; ExtNoCapVar) { }
                    group(ExtGroup)
                    {
                        field(ExtOptCtl; ExtOptVar) { Caption = 'Ext Option'; OptionCaption = 'Alpha Cap,Beta Cap'; }
                    }
                }
            }
            var
                ExtVar: Text[30];
                ExtNoCapVar: Text[30];
                ExtOptVar: Option Alpha,Beta;
        }
        """;

    private void Emit()
    {
        File.WriteAllText(Path.Combine(_root, "Spcc.al"), FixtureAl);
        var output = new BcCompiler().Emit(new[] { _root }, "SpccModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        typeof(RecordPatches).GetMethod("TryParsePageFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { FixtureAl });
    }

    private static int Id(string name)
    {
        Assert.True(AlObjectMetadataRegistry.TryGet("PageExtension", ExtensionId, out var ext));
        var m = System.Text.RegularExpressions.Regex.Match(ext!, $"ID=\"(\\d+)\" Name=\"{name}\"");
        Assert.True(m.Success, $"{name} not in the emitted delta document");
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public void AnAddedControl_AnswersItsDeclaredCaption_WholeAcrossASemicolon_AndInsideAGroup()
    {
        Emit();
        Assert.Equal("Ext Severity", RecordPatches.SourcePageExtensionControlCaption(PageId, Id("ExtRecCtl")));
        Assert.Equal("Ext; Semi Caption", RecordPatches.SourcePageExtensionControlCaption(PageId, Id("ExtVarCtl")));
        Assert.Equal("Ext Option", RecordPatches.SourcePageExtensionControlCaption(PageId, Id("ExtOptCtl")));
    }

    [Fact]
    public void AnAddedControlDeclaringNoCaption_AnswersNull_SoTheFieldCaptionOrControlNameChainRuns()
    {
        Emit();
        Assert.Null(RecordPatches.SourcePageExtensionControlCaption(PageId, Id("ExtRecNoCapCtl")));
        Assert.Null(RecordPatches.SourcePageExtensionControlCaption(PageId, Id("ExtNoCapCtl")));
        // The control name is what a page-variable control declaring none answers.
        Assert.Equal("ExtNoCapCtl", RecordPatches.SourcePageExtensionControlName(PageId, Id("ExtNoCapCtl")));
    }

    [Fact]
    public void AnAddedOptionControl_AnswersItsOptionCaption_AndOthersAnswerNull()
    {
        Emit();
        Assert.Equal("Alpha Cap,Beta Cap", RecordPatches.SourcePageExtensionControlOptionCaption(PageId, Id("ExtOptCtl")));
        Assert.Null(RecordPatches.SourcePageExtensionControlOptionCaption(PageId, Id("ExtVarCtl")));
    }

    [Fact]
    public void AnotherPage_OrAnIdNoExtensionAdds_BorrowsNothing()
    {
        Emit();
        var id = Id("ExtRecCtl");
        Assert.Null(RecordPatches.SourcePageExtensionControlCaption(OtherPageId, id));
        Assert.Null(RecordPatches.SourcePageExtensionControlOptionCaption(OtherPageId, Id("ExtOptCtl")));
        Assert.Null(RecordPatches.SourcePageExtensionControlCaption(PageId, 1));
    }
}
