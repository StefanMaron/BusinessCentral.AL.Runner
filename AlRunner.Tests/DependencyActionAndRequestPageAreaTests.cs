// Issues #4862 and #4863: a precompiled page's actions and a precompiled report's request-page
// controls reach the runner only through the symbol file, so BC's application-area pass never
// saw them. These pin the runner's own reading of each element's area and the removal sets built
// from it, including a precompiled pageextension's modify() of an action's area, which no corpus
// test can express (the corpus app is compiled from source). What BC answers for a TestPage over
// a precompiled page or report is the corpus's claim (the PR body's Corpus-PR: line).
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class DependencyActionAndRequestPageAreaTests
{
    private const int PageId = 88486201;
    private const int OddKindPageId = 88486202;
    private const int CompiledPageId = 88486203;
    private const int ReportId = 88486301;
    private const int CompiledReportId = 88486302;

    private const int OwnId = 648620001;        // Kind 2, own "#Service"
    private const int InheritsId = 648620002;   // Kind 2, none of its own; page states "#Jobs"
    private const int RefId = 648620003;        // Kind 4, none of its own, targets OwnId
    private const int SystemId = 648620004;     // Kind 6, none of its own: BC solves it to #All
    private const int ModifiedId = 648620005;   // Kind 2, own "#Service"; a precompiled ext modify()s it to "#Suite"
    private const int GroupId = 648620006;      // Kind 1, never area-tested
    private const int ExtOwnId = 648620011;     // added by a precompiled extension, own "#Basic"
    private const int ExtNoneId = 648620012;    // added by a precompiled extension, states none
    private const int OddKindId = 648620021;    // Kind 7 (file upload), none, on a page stating an area

    private const int RpOwnId = 648630001;      // request-page field, own "#Assembly"
    private const int RpInheritsId = 648630002; // request-page field, none; report states "#Basic,#Suite"
    private const int RpGroupId = 648630003;    // request-page group

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88486201, "Name": "DAA Page",
              "Properties": [ { "Name": "PageType", "Value": "List" },
                              { "Name": "ApplicationArea", "Value": "#Jobs" } ],
              "Actions": [
                { "Kind": 0, "Id": 1, "Name": "Processing", "Actions": [
                  { "Kind": 2, "Id": 648620001, "Name": "OwnAct",
                    "Properties": [ { "Name": "ApplicationArea", "Value": "#Service" } ] },
                  { "Kind": 2, "Id": 648620002, "Name": "InheritsAct" },
                  { "Kind": 1, "Id": 648620006, "Name": "Grp",
                    "Properties": [ { "Name": "ApplicationArea", "Value": "#Service" } ],
                    "Actions": [
                      { "Kind": 2, "Id": 648620005, "Name": "ModifiedAct",
                        "Properties": [ { "Name": "ApplicationArea", "Value": "#Service" } ] }
                    ] },
                  { "Kind": 6, "Id": 648620004, "Name": "SysAct" }
                ] },
                { "Kind": 0, "Id": 2, "Name": "Promoted", "Actions": [
                  { "Kind": 4, "Id": 648620003, "Name": "OwnAct_Promoted", "TargetName": "OwnAct" }
                ] }
              ]
            },
            {
              "Id": 88486202, "Name": "DAA Odd Page",
              "Properties": [ { "Name": "ApplicationArea", "Value": "#Jobs" } ],
              "Actions": [ { "Kind": 7, "Id": 648620021, "Name": "UploadAct" } ]
            },
            {
              "Id": 88486203, "Name": "DAA Compiled Page",
              "Properties": [ { "Name": "ApplicationArea", "Value": "#Jobs" } ],
              "Actions": [ { "Kind": 7, "Id": 648620031, "Name": "UploadAct2" } ]
            }
          ],
          "PageExtensions": [
            {
              "Id": 88486211, "Name": "DAA Page Ext", "TargetObject": "DAA Page",
              "ActionChanges": [
                { "Anchor": "Processing", "ChangeKind": 2,
                  "Actions": [
                    { "Kind": 2, "Id": 648620011, "Name": "ExtOwnAct",
                      "Properties": [ { "Name": "ApplicationArea", "Value": "#Basic" } ] },
                    { "Kind": 2, "Id": 648620012, "Name": "ExtNoneAct" }
                  ] },
                { "Anchor": "ModifiedAct", "ChangeKind": 9,
                  "Properties": [ { "Name": "ApplicationArea", "Value": "#Suite" } ] }
              ]
            }
          ],
          "Reports": [
            {
              "Id": 88486301, "Name": "DAA Report",
              "Properties": [ { "Name": "ApplicationArea", "Value": "#Basic,#Suite" } ],
              "RequestPage": {
                "Id": 0, "Name": "RequestOptionsPage",
                "Controls": [
                  { "Id": 1, "Name": "Content", "Controls": [
                    { "Kind": 1, "Id": 648630003, "Name": "Options", "Controls": [
                      { "Kind": 8, "Id": 648630001, "Name": "AsmNo",
                        "Properties": [ { "Name": "ApplicationArea", "Value": "#Assembly" },
                                        { "Name": "SourceExpression", "Value": "AsmNo" } ] },
                      { "Kind": 8, "Id": 648630002, "Name": "FromDate",
                        "Properties": [ { "Name": "SourceExpression", "Value": "FromDate" } ] }
                    ] }
                  ] }
                ]
              }
            },
            {
              "Id": 88486302, "Name": "DAA Compiled Report",
              "Properties": [ { "Name": "ApplicationArea", "Value": "#Basic,#Suite" } ],
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 648630011, "Name": "Opt",
                    "Properties": [ { "Name": "ApplicationArea", "Value": "#Assembly" } ] } ] } ] }
            },
            {
              "Id": 88486303, "Name": "DAA Ext Report",
              "Properties": [ { "Name": "ApplicationArea", "Value": "#Basic,#Suite" } ],
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 648630021, "Name": "BaseInherits" },
                  { "Kind": 8, "Id": 648630022, "Name": "BaseModified",
                    "Properties": [ { "Name": "ApplicationArea", "Value": "#Basic" } ] } ] } ] }
            },
            {
              "Id": 88486304, "Name": "DAA Conflict Report",
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 648630031, "Name": "Twice" } ] } ] }
            }
          ],
          "ReportExtensions": [
            {
              "Id": 88486311, "Name": "DAA Ext Report Ext", "Target": "DAA Ext Report",
              "RequestPage": { "Id": 0, "Name": "RequestPageExtension", "ControlChanges": [
                { "Anchor": "Content", "ChangeKind": 4, "Controls": [
                  { "Kind": 1, "Id": 648630029, "Name": "ExtGroup", "Controls": [
                    { "Kind": 8, "Id": 648630023, "Name": "ExtOwn",
                      "Properties": [ { "Name": "ApplicationArea", "Value": "#Manufacturing" } ] },
                    { "Kind": 8, "Id": 648630024, "Name": "ExtNone" } ] } ] },
                { "Anchor": "BaseModified", "ChangeKind": 9,
                  "Properties": [ { "Name": "ApplicationArea", "Value": "#Jobs" } ] } ] }
            },
            { "Id": 88486312, "Name": "DAA Conflict A", "Target": "DAA Conflict Report",
              "RequestPage": { "ControlChanges": [ { "Anchor": "Twice", "ChangeKind": 9,
                "Properties": [ { "Name": "ApplicationArea", "Value": "#Jobs" } ] } ] } },
            { "Id": 88486313, "Name": "DAA Conflict B", "Target": "DAA Conflict Report",
              "RequestPage": { "ControlChanges": [ { "Anchor": "Twice", "ChangeKind": 9,
                "Properties": [ { "Name": "ApplicationArea", "Value": "#Service" } ] } ] } }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-dep-action-area-tests");
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

    private static Dictionary<int, RecordPatches.DependencyActionArea> Actions(int pageId)
        => RecordPatches.DependencyActionAreas(pageId).ToDictionary(a => a.Id);

    private static bool BasicSuiteSession(string? area)
        => !string.IsNullOrWhiteSpace(area) && area.Split(',').Any(a => a is "#Basic" or "#Suite" or "#All");

    private static bool JobsSession(string? area)
        => !string.IsNullOrWhiteSpace(area) && area.Split(',').Any(a => a is "#Jobs" or "#All");

    [Fact]
    public void AnAction_AnswersItsOwnArea_ElseThePages()
        => WithDependencyApp(() =>
        {
            var actions = Actions(PageId);
            Assert.Equal("#Service", actions[OwnId].ApplicationArea);
            Assert.Equal("#Jobs", actions[InheritsId].ApplicationArea);
        });

    [Fact]
    public void AnActionRef_TakesNoAreaOfItsOwn_ASystemActionIsAll_AndGroupsAreNotAreaTested()
        => WithDependencyApp(() =>
        {
            var actions = Actions(PageId);
            Assert.Null(actions[RefId].ApplicationArea);
            Assert.Equal(OwnId, actions[RefId].TargetId);
            Assert.Equal("#All", actions[SystemId].ApplicationArea);
            Assert.False(actions.ContainsKey(GroupId));
        });

    [Fact]
    public void APrecompiledExtensionsActions_AnswerTheirOwnArea_OrNone_AndItsModifyReplaces()
        => WithDependencyApp(() =>
        {
            var actions = Actions(PageId);
            Assert.Equal("#Basic", actions[ExtOwnId].ApplicationArea);
            Assert.Null(actions[ExtNoneId].ApplicationArea);
            Assert.Equal("#Suite", actions[ModifiedId].ApplicationArea);
            Assert.Equal("#Suite", actions[ModifiedId].ModifiedArea);
        });

    [Fact]
    public void BasicSuiteSession_RemovesExactlyTheActionsItDoesNotEnable_AndTheActionRefWithItsTarget()
        => WithDependencyApp(() =>
        {
            var removed = ApplicationAreaControlRemoval.DependencyActionsToRemove(PageId, BasicSuiteSession)
                .OrderBy(id => id).ToArray();
            // Kept: ModifiedAct (#Suite after the modify), ExtOwnAct (#Basic), SysAct (#All).
            Assert.Equal(new[] { OwnId, InheritsId, RefId, ExtNoneId }.OrderBy(id => id).ToArray(), removed);
        });

    [Fact]
    public void JobsSession_KeepsThePageAreaAction_AndRemovesTheActionRefWhoseTargetIsRemoved()
        => WithDependencyApp(() =>
        {
            var removed = ApplicationAreaControlRemoval.DependencyActionsToRemove(PageId, JobsSession).ToHashSet();
            Assert.DoesNotContain(InheritsId, removed);
            Assert.Contains(OwnId, removed);
            Assert.Contains(RefId, removed);
        });

    [Fact]
    public void ASourceModify_ReplacesAPrecompiledActionsArea_AndConflictingWithAPrecompiledModify_Refuses()
        => WithDependencyApp(() =>
        {
            var removed = ApplicationAreaControlRemoval.DependencyActionsToRemove(
                PageId, BasicSuiteSession, new Dictionary<int, string> { [OwnId] = "#Basic" }).ToHashSet();
            Assert.DoesNotContain(OwnId, removed);
            Assert.DoesNotContain(RefId, removed);

            var ex = Assert.Throws<RunnerOutOfScopeException>(() => ApplicationAreaControlRemoval.DependencyActionsToRemove(
                PageId, BasicSuiteSession, new Dictionary<int, string> { [ModifiedId] = "#Jobs" }).ToList());
            Assert.Contains("'#Suite'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'#Jobs'", ex.Message, StringComparison.Ordinal);
        });

    [Fact]
    public void AnUnmeasuredActionKind_StatingNoArea_OnAPageThatStatesOne_Refuses()
        => WithDependencyApp(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.DependencyActionAreas(OddKindPageId));
            Assert.Contains($"action {OddKindId}", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Kind 7", ex.Message, StringComparison.Ordinal);
        });

    [Fact]
    public void ARequestPageField_AnswersItsOwnArea_ElseTheReports_AndGroupsAreNotAreaTested()
        => WithDependencyApp(() =>
        {
            var fields = RecordPatches.DependencyRequestPageFieldAreas(ReportId).ToDictionary(f => f.Id, f => f.ApplicationArea);
            Assert.Equal(new[] { RpOwnId, RpInheritsId }.OrderBy(i => i), fields.Keys.OrderBy(i => i));
            Assert.Equal("#Assembly", fields[RpOwnId]);
            Assert.Equal("#Basic,#Suite", fields[RpInheritsId]);
            Assert.Equal(new[] { RpOwnId },
                ApplicationAreaControlRemoval.DependencyRequestPageFieldsToRemove(ReportId, BasicSuiteSession).ToArray());
        });

    // #4896: a precompiled reportextension's request-page fields and modify().
    [Fact]
    public void APrecompiledReportExtension_AddsFieldsWithTheirOwnAreaOrNone_AndItsModifyReplaces()
        => WithDependencyApp(() =>
        {
            var fields = RecordPatches.DependencyRequestPageFieldAreas(88486303).ToDictionary(f => f.Id, f => f.ApplicationArea);
            Assert.Equal(new[] { 648630021, 648630022, 648630023, 648630024 }, fields.Keys.OrderBy(i => i));
            Assert.Equal("#Basic,#Suite", fields[648630021]);   // the report's
            Assert.Equal("#Jobs", fields[648630022]);           // the modify's, replacing #Basic
            Assert.Equal("#Manufacturing", fields[648630023]);  // the extension field's own
            Assert.Null(fields[648630024]);                     // none: not the report's
            Assert.Equal(new[] { 648630022, 648630023, 648630024 },
                ApplicationAreaControlRemoval.DependencyRequestPageFieldsToRemove(88486303, BasicSuiteSession).OrderBy(i => i));
        });

    [Fact]
    public void APrecompiledReportExtensionSharingItsIdWithASourceOne_Refuses_RatherThanBeingSkipped()
        => WithDependencyApp(() =>
        {
            var field = typeof(RecordPatches).GetField("_parsedReportExtensions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var parsed = (System.Collections.IDictionary)field.GetValue(null)!;
            parsed[88486311] = new ParsedReport(88486311, "Some Source Ext", IsExtension: true);
            try
            {
                var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.DependencyRequestPageFieldAreas(88486303).ToList());
                Assert.Contains("reportextension 88486311", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                parsed.Remove(88486311);
            }
        });

    [Fact]
    public void TwoPrecompiledReportExtensionsModifyingOneFieldToDifferentAreas_Refuse()
        => WithDependencyApp(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.DependencyRequestPageFieldAreas(88486304).ToList());
            Assert.Contains("'#Jobs'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'#Service'", ex.Message, StringComparison.Ordinal);
        });

    [Fact]
    public void APageOrReportTheRunnerCompiled_ContributesNothing()
        => WithDependencyApp(() =>
        {
            // Without a compiled document these answer (the odd page refuses, the report has a
            // rejected field); registered as compiled, both contribute nothing. Ids used by no
            // other test, so the registration left behind answers for nobody else.
            Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.DependencyActionAreas(CompiledPageId));
            Assert.Single(RecordPatches.DependencyRequestPageFieldAreas(CompiledReportId));
            AlPageMetadataRegistry.Register(CompiledPageId, $"<PageDefinition ID=\"{CompiledPageId}\" />");
            AlReportMetadataRegistry.Register(CompiledReportId, $"<ReportDefinition ID=\"{CompiledReportId}\" />");
            Assert.Empty(RecordPatches.DependencyActionAreas(CompiledPageId));
            Assert.Empty(RecordPatches.DependencyRequestPageFieldAreas(CompiledReportId));
        });
}
