// ActionRunObjectKindFromSourceTests — a precompiled page's RunObject NAME that two objects share
// is resolved by the kind the page's own AL source in the .app states (issue #4622).
//
// SymbolReference.json states RunObject as a bare name, so "Purchase Statistics" (page 161 and
// report 312 in Base Application) could only be refused. The .app's src/ file carries
// `RunObject = Page "Purchase Statistics";`, and that kind narrows the candidates. A symbols-only
// .app ships no source, so there the refusal stays. BC's own behaviour for the two kinds is pinned
// by corpus codeunit 60571; this class pins the runner reading the artifact.
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class ActionRunObjectKindFromSourceTests
{
    // Distinct from every other fixture: RecordPatches' dependency state is process-global.
    private const int HostPageId = 88246801;
    private const int SharedPageId = 88246802;
    private const int SharedReportId = 88246820;
    private const int ExtensionId = 88246850;

    private const int RunSharedAsReportAction = 88246901;
    private const int RunSharedAsPageAction = 88246902;
    private const int RunTwinAction = 88246903;
    private const int RunSharedDecoyCommentAction = 88246904;
    private const int ExtensionRunSharedAction = 88246905;

    private const string HostSourcePath = "Fixture/AROKS Host.Page.al";
    private const string ExtensionSourcePath = "Fixture/AROKS Host Ext.PageExt.al";

    private static string Action(int id, string name, string runObject)
        => $$"""
            { "Kind": 2, "Id": {{id}}, "Name": "{{name}}",
              "Properties": [ { "Name": "RunObject", "Value": "{{runObject}}" } ] }
            """;

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "17.0",
          "Tables": [
            {
              "Id": 88246800, "Name": "AROKS Row",
              "Fields": [ { "Id": 1, "Name": "No.", "TypeDefinition": { "Name": "Code[20]" }, "Properties": [] } ],
              "Keys": [ { "Name": "PK", "FieldNames": [ "No." ], "Properties": [] } ],
              "Properties": []
            }
          ],
          "Codeunits": [
            { "Id": 88246811, "Name": "AROKS Twin" },
            { "Id": 88246812, "Name": "AROKS Twin" }
          ],
          "Reports": [ { "Id": {{SharedReportId}}, "Name": "AROKS Shared" } ],
          "Pages": [
            {
              "Id": {{HostPageId}}, "Name": "AROKS Host",
              "ReferenceSourceFileName": "{{HostSourcePath}}",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "88246800" } ],
              "Actions": [
                {{Action(RunSharedAsReportAction, "Shared Report Action", "AROKS Shared")}},
                {{Action(RunSharedAsPageAction, "SharedPageAction", "AROKS Shared")}},
                {{Action(RunTwinAction, "RunTwin", "AROKS Twin")}},
                {{Action(RunSharedDecoyCommentAction, "DecoyComment", "AROKS Shared")}}
              ]
            },
            {
              "Id": {{SharedPageId}}, "Name": "AROKS Shared",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "88246800" } ]
            }
          ],
          "PageExtensions": [
            {
              "Id": {{ExtensionId}}, "Name": "AROKS Host Ext", "TargetObject": "AROKS Host",
              "ReferenceSourceFileName": "{{ExtensionSourcePath}}",
              "ActionChanges": [
                { "Anchor": "SharedPageAction", "ChangeKind": 2,
                  "Actions": [ {{Action(ExtensionRunSharedAction, "ExtShared", "AROKS Shared")}} ] }
              ]
            }
          ]
        }
        """;

    // The action names are spelled differently from the symbol file on purpose (case, quoting):
    // AL resolves names case-insensitively, and the symbol file writes a quoted name unquoted.
    // DecoyComment's comment names a Report kind; a text scan would read it, the parser does not.
    private const string HostSource = """
        page 88246801 "AROKS Host"
        {
            PageType = List;
            SourceTable = "AROKS Row";

            actions
            {
                area(Processing)
                {
                    action("shared report action")
                    {
                        Caption = 'Opens { a report }';
                        RunObject = Report "AROKS Shared";
                    }
                    action(SharedPageAction)
                    {
                        RunObject = page "AROKS Shared";
                    }
                    action(RunTwin)
                    {
                        RunObject = Codeunit "AROKS Twin";
                    }
                    action(DecoyComment)
                    {
                        // RunObject = Report "AROKS Shared";
                        RunObject = Page "AROKS Shared";
                    }
                }
            }
        }
        """;

    private const string ExtensionSource = """
        pageextension 88246850 "AROKS Host Ext" extends "AROKS Host"
        {
            actions
            {
                addafter(SharedPageAction)
                {
                    action(ExtShared)
                    {
                        RunObject = Report "AROKS Shared";
                    }
                }
            }
        }
        """;

    private static string WriteApp(string dir, bool withSource)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var w = new StreamWriter(za.CreateEntry(name).Open(), Encoding.UTF8);
            w.Write(text);
        }
        Entry("SymbolReference.json", SymbolReference);
        // The real layout: the symbol file states the path relative to src/.
        if (withSource)
        {
            Entry("src/" + HostSourcePath, HostSource);
            Entry("src/" + ExtensionSourcePath, ExtensionSource);
        }
        return appPath;
    }

    private static void WithLoadedDependency(bool withSource, Action body)
    {
        var dir = TestScratch.Dir("al-runner-action-runobject-kind-source-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, withSource));
            body();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static (string Kind, int ObjectId) Resolve(int actionId)
    {
        var type = typeof(RunnerPageInstance);
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_pageId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, HostPageId);
        var method = type.GetMethod("ResolveRunTargetFromSymbols", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RunnerPageInstance.ResolveRunTargetFromSymbols not found");

        object? boxed;
        try { boxed = method.Invoke(instance, new object?[] { actionId }); }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }

        Assert.NotNull(boxed);
        var t = boxed!.GetType();
        return (t.GetProperty("Kind")!.GetValue(boxed)!.ToString()!, (int)t.GetProperty("ObjectId")!.GetValue(boxed)!);
    }

    [Fact]
    public void SharedName_SourceSaysReport_ResolvesToTheReport()
        => WithLoadedDependency(withSource: true, () =>
        {
            var target = Resolve(RunSharedAsReportAction);
            Assert.Equal("Report", target.Kind);
            Assert.Equal(SharedReportId, target.ObjectId);
        });

    [Fact]
    public void SharedName_SourceSaysPage_ResolvesToThePage()
        => WithLoadedDependency(withSource: true, () =>
        {
            var target = Resolve(RunSharedAsPageAction);
            Assert.Equal("Page", target.Kind);
            Assert.Equal(SharedPageId, target.ObjectId);
        });

    // A comment naming the other kind is trivia to BC's parser, not a second RunObject.
    [Fact]
    public void SharedName_CommentedOutRunObjectOfTheOtherKind_IsIgnored()
        => WithLoadedDependency(withSource: true, () =>
        {
            var target = Resolve(RunSharedDecoyCommentAction);
            Assert.Equal("Page", target.Kind);
            Assert.Equal(SharedPageId, target.ObjectId);
        });

    // Declared by a pageextension: its own source file answers, not the base page's.
    [Fact]
    public void SharedName_DeclaredByAPageExtension_ReadsTheExtensionsSource()
        => WithLoadedDependency(withSource: true, () =>
        {
            var target = Resolve(ExtensionRunSharedAction);
            Assert.Equal("Report", target.Kind);
            Assert.Equal(SharedReportId, target.ObjectId);
        });

    // The kind is Codeunit and two codeunits carry the name: the source narrows nothing.
    [Fact]
    public void TwoObjectsOfTheStatedKind_StillRefused()
        => WithLoadedDependency(withSource: true, () =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Resolve(RunTwinAction));
            Assert.Contains("codeunit 88246811", ex.Message);
            Assert.Contains("codeunit 88246812", ex.Message);
            Assert.Contains("#4622", ex.Message);
        });

    // A symbols-only .app ships no src/: nothing states the kind, so the refusal stays and says why.
    [Fact]
    public void SharedName_SymbolsOnlyApp_StillRefusedNamingTheMissingSource()
        => WithLoadedDependency(withSource: false, () =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Resolve(RunSharedAsPageAction));
            Assert.Contains($"page {SharedPageId}", ex.Message);
            Assert.Contains($"report {SharedReportId}", ex.Message);
            Assert.Contains("ships no AL source", ex.Message);
        });
}
