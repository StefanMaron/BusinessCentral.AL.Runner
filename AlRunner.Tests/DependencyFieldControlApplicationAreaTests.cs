// Issue #4796: a precompiled page's runtime metadata (DependencyPageMetadataXml) carries no field
// controls, so BC's application-area pass never removed one and a TestPage found a control whose
// area the session does not enable. These pin the runner's own reading of each field control's
// area out of SymbolReference.json, and the removal set built from it. What BC answers for a
// TestPage over a precompiled page is the corpus's claim (the PR body's Corpus-PR: line).
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches' dependency page state resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyFieldControlApplicationAreaTests
{
    private const int PageId = 88479601;
    private const int NoAreaPageId = 88479602;
    private const int CompiledPageId = 88479603;

    private const int ServiceId = 647960001;     // own "#Service"
    private const int InheritsId = 647960002;    // none of its own, page states "#Basic,#Suite"
    private const int NestedAllId = 647960003;   // inside a group, own "#All"
    private const int ModifiedId = 647960004;    // own "#Basic", an extension modify()s it to "#Jobs"
    private const int ExtOwnId = 647960011;      // added by an extension, own "#Service"
    private const int ExtNoneId = 647960012;     // added by an extension, states none
    private const int NoAreaId = 647960021;      // on a page stating no area, states none
    private const int CompiledCtlId = 647960031;

    // The group carries "#Service": BC's RemoveControl checks a ControlDefinition only, and a
    // group is a ControlGroupDefinition, so the group's area must not reach the field inside it.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88479601, "Name": "DFCA Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" },
                              { "Name": "ApplicationArea", "Value": "#Basic,#Suite" } ],
              "Controls": [
                { "Kind": 1, "Id": 1, "Name": "content", "Controls": [
                  { "Kind": 8, "Id": 647960001, "Name": "ServiceCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" },
                                    { "Name": "ApplicationArea", "Value": "#Service" } ] },
                  { "Kind": 8, "Id": 647960002, "Name": "InheritsCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Name" } ] },
                  { "Kind": 7, "Id": 2, "Name": "ServiceGroup",
                    "Properties": [ { "Name": "ApplicationArea", "Value": "#Service" } ],
                    "Controls": [
                      { "Kind": 8, "Id": 647960003, "Name": "NestedAllCtl",
                        "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Name" },
                                        { "Name": "ApplicationArea", "Value": "#All" } ] }
                    ] },
                  { "Kind": 8, "Id": 647960004, "Name": "ModifiedCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" },
                                    { "Name": "ApplicationArea", "Value": "#Basic" } ] }
                ] }
              ]
            },
            {
              "Id": 88479602, "Name": "DFCA No Area Page",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Controls": [
                { "Kind": 1, "Id": 1, "Name": "content", "Controls": [
                  { "Kind": 8, "Id": 647960021, "Name": "NoAreaCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" } ] }
                ] }
              ]
            },
            {
              "Id": 88479603, "Name": "DFCA Compiled Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Controls": [
                { "Kind": 1, "Id": 1, "Name": "content", "Controls": [
                  { "Kind": 8, "Id": 647960031, "Name": "CompiledCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" },
                                    { "Name": "ApplicationArea", "Value": "#Service" } ] }
                ] }
              ]
            }
          ],
          "PageExtensions": [
            {
              "Id": 88479611, "Name": "DFCA Page Ext", "TargetObject": "DFCA Page",
              "ControlChanges": [
                { "Anchor": "content", "ChangeKind": 2,
                  "Controls": [
                    { "Kind": 8, "Id": 647960011, "Name": "ExtServiceCtl",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Code" },
                                      { "Name": "ApplicationArea", "Value": "#Service" } ] },
                    { "Kind": 8, "Id": 647960012, "Name": "ExtNoneCtl",
                      "Properties": [ { "Name": "SourceExpression", "Value": "Rec.Name" } ] }
                  ] },
                { "Anchor": "ModifiedCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "ApplicationArea", "Value": "#Jobs" } ] }
              ]
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-dep-field-control-area-tests");
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

    private static Dictionary<int, string?> Areas(int pageId)
        => RecordPatches.DependencyFieldControlAreas(pageId).ToDictionary(c => c.Id, c => c.ApplicationArea);

    // NavSession.IsApplicationAreaEnabled for a session whose areas are "#Basic,#Suite": a blank
    // area is never enabled, otherwise any listed area matches.
    private static bool BasicSuiteSession(string? area)
        => !string.IsNullOrWhiteSpace(area)
           && area.Split(',').Any(a => a is "#Basic" or "#Suite" or "#All");

    [Fact]
    public void BasePageControls_AnswerTheirOwnAreaElseThePages()
        => WithDependencyApp(() =>
        {
            var areas = Areas(PageId);
            Assert.Equal("#Service", areas[ServiceId]);
            Assert.Equal("#Basic,#Suite", areas[InheritsId]);
            Assert.Equal("#All", areas[NestedAllId]);
        });

    [Fact]
    public void ExtensionControls_AnswerTheirOwnArea_NeverTheBasePages()
        => WithDependencyApp(() =>
        {
            var areas = Areas(PageId);
            Assert.Equal("#Service", areas[ExtOwnId]);
            Assert.Null(areas[ExtNoneId]);
        });

    [Fact]
    public void ModifyOfApplicationArea_ReplacesTheDeclaredOne()
        => WithDependencyApp(() => Assert.Equal("#Jobs", Areas(PageId)[ModifiedId]));

    [Fact]
    public void ControlOnAPageStatingNoArea_AnswersNull()
        => WithDependencyApp(() => Assert.Null(Areas(NoAreaPageId)[NoAreaId]));

    [Fact]
    public void BasicSuiteSession_RemovesExactlyTheControlsWhoseAreaItDoesNotEnable()
        => WithDependencyApp(() =>
        {
            var removed = ApplicationAreaControlRemoval.DependencyFieldControlsToRemove(PageId, BasicSuiteSession)
                .OrderBy(id => id).ToArray();
            // Kept: InheritsCtl (page's #Basic,#Suite), NestedAllCtl (#All despite its #Service
            // group). Removed: own #Service, the #Jobs modify, the extension's #Service and its
            // area-less control.
            Assert.Equal(new[] { ServiceId, ModifiedId, ExtOwnId, ExtNoneId }, removed);

            Assert.Equal(new[] { NoAreaId },
                ApplicationAreaControlRemoval.DependencyFieldControlsToRemove(NoAreaPageId, BasicSuiteSession));
        });

    // #4866: a source pageextension's modify() of ApplicationArea, applied on the precompiled path.
    [Fact]
    public void SourceModifyOfApplicationArea_ReplacesAPrecompiledControlsArea()
        => WithDependencyApp(() =>
        {
            var changes = new Dictionary<int, string> { [InheritsId] = "#Service", [ServiceId] = "#Suite" };
            var removed = ApplicationAreaControlRemoval.DependencyFieldControlsToRemove(PageId, BasicSuiteSession, changes)
                .OrderBy(id => id).ToArray();
            // InheritsCtl moves out of #Basic,#Suite and is removed; ServiceCtl moves into #Suite and is kept.
            Assert.Equal(new[] { InheritsId, ModifiedId, ExtOwnId, ExtNoneId }.OrderBy(id => id).ToArray(), removed);
        });

    [Fact]
    public void SourceAndPrecompiledModifyToDifferentAreas_Refuse_ToTheSameAreaDoNot()
        => WithDependencyApp(() =>
        {
            var ex = Assert.Throws<AlRunner.Infrastructure.RunnerOutOfScopeException>(() =>
                ApplicationAreaControlRemoval.DependencyFieldControlsToRemove(
                    PageId, BasicSuiteSession, new Dictionary<int, string> { [ModifiedId] = "#Suite" }).ToList());
            Assert.Contains("'#Jobs'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'#Suite'", ex.Message, StringComparison.Ordinal);

            Assert.Contains(ModifiedId, ApplicationAreaControlRemoval.DependencyFieldControlsToRemove(
                PageId, BasicSuiteSession, new Dictionary<int, string> { [ModifiedId] = "#Jobs" }));
        });

    [Fact]
    public void PageTheRunnerCompiled_ContributesNothing_BcsOwnPassReadsItsRealControls()
        => WithDependencyApp(() =>
        {
            Assert.Equal(new[] { CompiledCtlId }, Areas(CompiledPageId).Keys);
            AlPageMetadataRegistry.Register(CompiledPageId, $"<PageDefinition ID=\"{CompiledPageId}\" />");
            Assert.Empty(RecordPatches.DependencyFieldControlAreas(CompiledPageId));
        });

    [Fact]
    public void UnknownPage_ContributesNothing()
        => WithDependencyApp(() => Assert.Empty(RecordPatches.DependencyFieldControlAreas(88479699)));
}
