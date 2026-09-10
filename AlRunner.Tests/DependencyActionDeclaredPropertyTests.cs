// Issue #2460, the ACTION half of #3504's defect, in the same method and the same file.
//
// DependencyPageMetadataXml reconstructs no control tree AND no action tree for a page shipping
// precompiled in a dependency .app, so RunnerPageInstance.ActionDefinition(id) — which resolves
// through form.MetadataHelper.TryGetCommonActionDefinitionById over that empty tree — answers
// null for every action, and EvaluateProperty's `raw is null => the AL declared none` arm returns
// the AL default. Every action on every such page reported Enabled = true and Visible = true
// whatever the page's AL says, so Assert.IsTrue(action.Enabled()) passed vacuously and only
// Assert.IsFalse caught it. Six measured failures in Tests-SINGLESERVER at BC 28.1.
//
// The symbol file DOES state both, per action, and the walk that reaches them already exists
// (CollectMemberNames over the "Actions" tree, which reads Name / TargetName / RunObject off the
// very same nodes). These tests pin that the declaration now survives that walk.
//
// Measured on Base Application 28.1.49838.53910: 25,184 actions across 2,610 pages, of which
// 1,129 declare Enabled and 1,101 declare Visible. Page 977 "Time Sheet Setup Wizard" — #2460's
// own surface — declares Enabled on exactly the three actions the issue names:
//     BackAction    Enabled = 'BackActionEnabled'
//     NextAction    Enabled = 'NextActionEnabled'
//     FinishAction  Enabled = 'FinishActionEnabled'
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyControlDeclaredPropertyTests: RecordPatches' dependency page state
// resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyActionDeclaredPropertyTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    private const int PageId = 88440501;

    private const int BackActionId = 640647001;    // Enabled = "BackActionEnabled"
    private const int FinishActionId = 640647002;  // Enabled = "FinishActionEnabled", Visible = "false"
    private const int PlainActionId = 640647003;   // declares neither
    private const int NestedActionId = 640647004;  // inside a group — the walk must descend
    private const int UnknownActionId = 640647099; // declared by nothing

    // Modelled on Base Application page 977 "Time Sheet Setup Wizard", whose three wizard
    // actions declare Enabled bound to page globals its UpdateControls() assigns. The nested
    // action is what proves the collector descends the Actions tree rather than reading only
    // its top level — a real page's actions live inside area()/group() nodes.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88440501,
              "Name": "DADP Dep Page",
              "Properties": [
                { "Name": "PageType", "Value": "NavigatePage" }
              ],
              "Actions": [
                {
                  "Kind": 2,
                  "Id": 640647001,
                  "Name": "BackAction",
                  "Properties": [ { "Name": "Enabled", "Value": "BackActionEnabled" } ]
                },
                {
                  "Kind": 2,
                  "Id": 640647002,
                  "Name": "FinishAction",
                  "Properties": [
                    { "Name": "Enabled", "Value": "FinishActionEnabled" },
                    { "Name": "Visible", "Value": "false" }
                  ]
                },
                {
                  "Kind": 2,
                  "Id": 640647003,
                  "Name": "PlainAction",
                  "Properties": []
                },
                {
                  "Kind": 3,
                  "Id": 640647010,
                  "Name": "ProcessingGroup",
                  "Actions": [
                    {
                      "Kind": 2,
                      "Id": 640647004,
                      "Name": "NestedAction",
                      "Properties": [ { "Name": "Enabled", "Value": "NestedEnabled" } ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-dep-action-declared-property-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── the RED ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ActionEnabledBoundToAPageGlobal_IsReadBackVerbatim()
        => WithDependencyApp(() =>
        {
            // #2460's own shape, name for name. Verbatim rather than resolved: the value is
            // only knowable by evaluating the page global against live page state, which is
            // EvaluateProperty's registered-expression lookup, not this layer's job.
            Assert.Equal("BackActionEnabled",
                RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, BackActionId, "Enabled"));
        });

    [Fact]
    public void ActionDeclaringBoth_ResolvesEachIndependently()
        => WithDependencyApp(() =>
        {
            // Keyed on the property NAME, so one action declaring both cannot answer one for
            // the other — the same discipline the control resolver keeps.
            Assert.Equal("FinishActionEnabled",
                RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, FinishActionId, "Enabled"));
            Assert.Equal("false",
                RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, FinishActionId, "Visible"));
        });

    [Fact]
    public void ActionNestedInAGroup_IsReachedByTheWalk()
        => WithDependencyApp(() =>
        {
            // Load-bearing: on a real page every action sits inside area()/group() nodes, so a
            // collector reading only the top level of Actions would find none of them and the
            // whole fix would be dead on arrival while these tests still passed.
            Assert.Equal("NestedEnabled",
                RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, NestedActionId, "Enabled"));
        });

    // ── negatives ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ActionDeclaringNeither_AnswersNull()
        => WithDependencyApp(() =>
        {
            // null must keep meaning "declares none", whose answer is the AL default of true.
            // An action really is enabled and visible unless its AL says otherwise.
            Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, PlainActionId, "Enabled"));
            Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, PlainActionId, "Visible"));
        });

    [Fact]
    public void ActionTheDependencyDoesNotDeclare_AnswersNull()
        => WithDependencyApp(() =>
            Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, UnknownActionId, "Enabled")));

    [Fact]
    public void PageNoDependencyDeclares_AnswersNull()
        => WithDependencyApp(() =>
            Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(88440502, BackActionId, "Enabled")));

    [Fact]
    public void UnknownPropertyName_AnswersNull()
        => WithDependencyApp(() =>
            // An action has no Editable in AL, so only Enabled and Visible resolve here. Asking
            // for a third must answer null rather than being mapped onto one of the two.
            Assert.Null(RecordPatches.TryGetDependencyActionDeclaredProperty(PageId, BackActionId, "Editable")));
}
