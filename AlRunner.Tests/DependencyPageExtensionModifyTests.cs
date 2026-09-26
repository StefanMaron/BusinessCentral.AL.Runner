// Issue #4761: a PRECOMPILED pageextension's modify(<member>) { Enabled/Visible/Editable = ...; }
// over a member of a PRECOMPILED page was ignored, so the member kept its own declared value or
// the AL default of true. These pin the runner's own reading of SymbolReference.json; the BC
// behaviour is corpus codeunit 67403 (StefanMaron/BusinessCentral.AL.Language.Tests#458).
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPageExtensionModifyTests
{
    private const int PageId = 88247601;
    private const int OtherPageId = 88247602;
    private const int ConflictPageId = 88247603;
    private const int ModifiedControlId = 646700001;
    private const int UntouchedControlId = 646700002;
    private const int OtherPageControlId = 646700003;
    private const int ConflictControlId = 646700004;
    private const int AgreedControlId = 646700005;
    private const int ModifiedActionId = 646700011;
    private const int UntouchedActionId = 646700012;
    private const int ModifyingExtId = 88247611;

    // "MDX Page" declares Enabled = BaseFlag on CodeCtl and Enabled = ActionFlag on DoIt.
    // Extension 88247611 modifies CodeCtl's Enabled and DoIt's Visible (spelled "visible", as
    // Base Application spells one of its own) and Enabled. Extension 88247612 modifies a control
    // of the same NAME on a different page, which must not leak. On "MDX Conflict Page" two
    // extensions disagree about one control and agree about another.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88247601, "Name": "MDX Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Controls": [
                { "Kind": 1, "Id": 1, "Name": "content", "Controls": [
                  { "Kind": 8, "Id": 646700001, "Name": "CodeCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" },
                                    { "Name": "Enabled", "Value": "BaseFlag" } ] },
                  { "Kind": 8, "Id": 646700002, "Name": "Untouched",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Name" },
                                    { "Name": "Enabled", "Value": "false" } ] }
                ] }
              ],
              "Actions": [
                { "Kind": 0, "Id": 646700011, "Name": "DoIt",
                  "Properties": [ { "Name": "Enabled", "Value": "ActionFlag" } ] },
                { "Kind": 0, "Id": 646700012, "Name": "Other" }
              ]
            },
            {
              "Id": 88247602, "Name": "MDX Other Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Controls": [
                { "Kind": 1, "Id": 1, "Name": "content", "Controls": [
                  { "Kind": 8, "Id": 646700003, "Name": "CodeCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" } ] }
                ] }
              ]
            },
            {
              "Id": 88247603, "Name": "MDX Conflict Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Controls": [
                { "Kind": 1, "Id": 1, "Name": "content", "Controls": [
                  { "Kind": 8, "Id": 646700004, "Name": "Disputed",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" } ] },
                  { "Kind": 8, "Id": 646700005, "Name": "Agreed",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Name" } ] }
                ] }
              ]
            }
          ],
          "PageExtensions": [
            {
              "Id": 88247611, "Name": "MDX Page Ext", "TargetObject": "MDX Page",
              "ControlChanges": [
                { "Anchor": "CodeCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "Enabled", "Value": "Rec.Code <> ''" } ] }
              ],
              "ActionChanges": [
                { "Anchor": "DoIt", "ChangeKind": 9,
                  "Properties": [ { "Name": "Enabled", "Value": "false" },
                                  { "Name": "visible", "Value": "false" } ] }
              ]
            },
            {
              "Id": 88247612, "Name": "MDX Other Page Ext", "TargetObject": "MDX Other Page",
              "ControlChanges": [
                { "Anchor": "CodeCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "Editable", "Value": "false" } ] }
              ]
            },
            {
              "Id": 88247613, "Name": "MDX Conflict Ext A", "TargetObject": "MDX Conflict Page",
              "ControlChanges": [
                { "Anchor": "Disputed", "ChangeKind": 9,
                  "Properties": [ { "Name": "Visible", "Value": "false" } ] },
                { "Anchor": "Agreed", "ChangeKind": 9,
                  "Properties": [ { "Name": "Editable", "Value": "false" } ] }
              ]
            },
            {
              "Id": 88247614, "Name": "MDX Conflict Ext B", "TargetObject": "MDX Conflict Page",
              "ControlChanges": [
                { "Anchor": "Disputed", "ChangeKind": 9,
                  "Properties": [ { "Name": "Visible", "Value": "true" } ] },
                { "Anchor": "Agreed", "ChangeKind": 9,
                  "Properties": [ { "Name": "Editable", "Value": "false" } ] }
              ]
            }
          ]
        }
        """;

    private static IDictionary ParsedPageExtensions =>
        (IDictionary)typeof(RecordPatches).GetField("_parsedPageExtensions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-dep-pageext-modify-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);
            RecordPatches.AddBcAppPath(appPath);
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ModifiedControl_AnswersTheExtensionsValue_OverItsOwn()
        => WithDependencyApp(() =>
        {
            Assert.Equal("Rec.Code <> ''", RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, ModifiedControlId, "Enabled"));
            // A property the extension does not modify keeps the control's own answer (none).
            Assert.Null(RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, ModifiedControlId, "Editable"));
            Assert.Equal("false", RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, UntouchedControlId, "Enabled"));
        });

    [Fact]
    public void ModifiedAction_AnswersTheExtensionsValue_IncludingALowercasePropertyName()
        => WithDependencyApp(() =>
        {
            Assert.Equal("false", RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, ModifiedActionId, "Enabled"));
            // DoIt declares no Visible of its own; the extension's "visible" is the only answer.
            Assert.Equal("false", RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, ModifiedActionId, "Visible"));
            Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, UntouchedActionId, "Enabled"));
        });

    [Fact]
    public void ModifyOfASameNamedControlOnAnotherPage_DoesNotLeak()
        => WithDependencyApp(() =>
        {
            Assert.Null(RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, ModifiedControlId, "Editable"));
            Assert.Equal("false", RecordPatches.TryGetDependencyControlDeclaredProperty(OtherPageId, OtherPageControlId, "Editable"));
        });

    [Fact]
    public void TwoExtensionsDisagreeing_Refuses_AndAgreeing_Answers()
        => WithDependencyApp(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(
                () => RecordPatches.TryGetDependencyControlDeclaredProperty(ConflictPageId, ConflictControlId, "Visible"));
            Assert.Contains("88247613", ex.Message);
            Assert.Contains("88247614", ex.Message);
            Assert.Equal("false", RecordPatches.TryGetDependencyControlDeclaredProperty(ConflictPageId, AgreedControlId, "Editable"));
        });

    [Fact]
    public void SameNumberedSourcePageExtension_ReplacesThePrecompiledModify()
        => WithDependencyApp(() =>
        {
            // A source-parsed extension with the precompiled one's id stands in for it, as in
            // DependencyPageExtensionFieldControls, so the precompiled modify no longer applies.
            typeof(RecordPatches).GetMethod("TryParsePageFile", BindingFlags.NonPublic | BindingFlags.Static)!
                .InvokeStatic($$"""
                    pageextension {{ModifyingExtId}} "MDX Page Ext" extends "MDX Page"
                    {
                    }
                    """);
            Assert.True(ParsedPageExtensions.Contains(ModifyingExtId));
            try
            {
                Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, ModifiedActionId, "Visible"));
                Assert.Equal("BaseFlag", RecordPatches.TryGetDependencyControlDeclaredProperty(PageId, ModifiedControlId, "Enabled"));
            }
            finally
            {
                ParsedPageExtensions.Remove(ModifyingExtId);
            }
        });
}
