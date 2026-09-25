// ActionRunObjectKindFromSymbolsTests — pins how RunnerPageInstance.ResolveRunTargetFromSymbols
// turns a precompiled page's RunObject NAME into an object kind and id (issue #4582).
//
// SymbolReference.json states an action's RunObject as a bare name with no object type, so the
// kind has to come from the run's object inventory. Before #4582 only the PAGE inventory was
// asked, and a name that was a codeunit, report, xmlport or query was refused as "not a page".
// The rule now: a name that resolves to exactly one object across the five RunObject kinds IS
// that object; a name two objects share stays a loud refusal (#4622).
//
// A C# test rather than an AL one because the subject is the runner reading a Microsoft build
// artifact that a real service tier never reads (it has compiled page metadata instead). BC's
// own behaviour for each kind is pinned by corpus codeunit 60559. Same shape and reasoning as
// ActionRunPageLinkRefusalTests, whose header has the long form.
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class ActionRunObjectKindFromSymbolsTests
{
    // Ids distinct from every other fixture: RecordPatches' dependency state is process-global.
    private const int HostPageId = 88245801;
    private const int TargetPageId = 88245802;
    private const int CodeunitId = 88245810;
    private const int ReportId = 88245820;
    private const int XmlPortId = 88245830;
    private const int QueryId = 88245840;

    private const int RunCodeunitAction = 88245901;
    private const int RunReportAction = 88245902;
    private const int RunXmlPortAction = 88245903;
    private const int RunQueryAction = 88245904;
    private const int RunPageAction = 88245905;
    private const int RunSharedAction = 88245906;
    private const int RunNowhereAction = 88245907;
    private const int RunTwoCodeunitsAction = 88245908;

    private static string Action(int id, string name, string runObject, string extraProps = "")
        => $$"""
            { "Kind": 2, "Id": {{id}}, "Name": "{{name}}",
              "Properties": [ { "Name": "RunObject", "Value": "{{runObject}}" }{{extraProps}} ] }
            """;

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "17.0",
          "Tables": [
            {
              "Id": 88245800, "Name": "AROK Row",
              "Fields": [ { "Id": 1, "Name": "No.", "TypeDefinition": { "Name": "Code[20]" }, "Properties": [] } ],
              "Keys": [ { "Name": "PK", "FieldNames": [ "No." ], "Properties": [] } ],
              "Properties": []
            }
          ],
          "Codeunits": [
            { "Id": {{CodeunitId}}, "Name": "AROK Codeunit" },
            { "Id": 88245811, "Name": "AROK Shared" },
            { "Id": 88245812, "Name": "AROK Twin" },
            { "Id": 88245813, "Name": "AROK Twin" }
          ],
          "Reports": [ { "Id": {{ReportId}}, "Name": "AROK Report" } ],
          "XmlPorts": [ { "Id": {{XmlPortId}}, "Name": "AROK XmlPort" } ],
          "Queries": [ { "Id": {{QueryId}}, "Name": "AROK Query" } ],
          "Pages": [
            {
              "Id": {{HostPageId}}, "Name": "AROK Host",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "88245800" } ],
              "Actions": [
                {{Action(RunCodeunitAction, "RunCodeunit", "AROK Codeunit", """, { "Name": "RunPageOnRec", "Value": "1" }""")}},
                {{Action(RunReportAction, "RunReport", "AROK Report")}},
                {{Action(RunXmlPortAction, "RunXmlPort", "AROK XmlPort")}},
                {{Action(RunQueryAction, "RunQuery", "AROK Query")}},
                {{Action(RunPageAction, "RunPage", "AROK Target")}},
                {{Action(RunSharedAction, "RunShared", "AROK Shared")}},
                {{Action(RunNowhereAction, "RunNowhere", "AROK Nowhere")}},
                {{Action(RunTwoCodeunitsAction, "RunTwin", "AROK Twin")}}
              ]
            },
            {
              "Id": {{TargetPageId}}, "Name": "AROK Target",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "88245800" } ]
            },
            {
              "Id": 88245803, "Name": "AROK Shared",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "88245800" } ]
            }
          ]
        }
        """;

    private static string WriteApp(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static void WithLoadedDependency(Action body)
    {
        var dir = TestScratch.Dir("al-runner-action-runobject-kind-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));
            body();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ResolveRunTargetFromSymbols reads only _pageId from the instance (to find the symbol file's
    // action map and to name the page in a refusal), so an uninitialised instance runs it without
    // a live NavForm.
    private static (string Kind, int ObjectId, string? Name, bool RunPageOnRec, int LinkCount) Resolve(int actionId)
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
        object? Get(string p) => t.GetProperty(p)!.GetValue(boxed);
        return (Get("Kind")!.ToString()!, (int)Get("ObjectId")!, (string?)Get("ObjectName"),
            (bool)Get("RunPageOnRec")!, ((System.Collections.ICollection)Get("Links")!).Count);
    }

    [Fact]
    public void CodeunitName_ResolvesToThatCodeunit()
        => WithLoadedDependency(() =>
        {
            var target = Resolve(RunCodeunitAction);
            Assert.Equal("Codeunit", target.Kind);
            Assert.Equal(CodeunitId, target.ObjectId);
            Assert.Equal("AROK Codeunit", target.Name);
            // Carried through: the codeunit route ignores it, but it is what the AL declared.
            Assert.True(target.RunPageOnRec);
            Assert.Equal(0, target.LinkCount);
        });

    [Fact]
    public void ReportName_ResolvesToThatReport()
        => WithLoadedDependency(() =>
        {
            var target = Resolve(RunReportAction);
            Assert.Equal("Report", target.Kind);
            Assert.Equal(ReportId, target.ObjectId);
        });

    [Fact]
    public void XmlPortName_ResolvesToThatXmlPort()
        => WithLoadedDependency(() =>
        {
            var target = Resolve(RunXmlPortAction);
            Assert.Equal("XMLport", target.Kind);
            Assert.Equal(XmlPortId, target.ObjectId);
        });

    [Fact]
    public void QueryName_ResolvesToThatQuery()
        => WithLoadedDependency(() =>
        {
            var target = Resolve(RunQueryAction);
            Assert.Equal("Query", target.Kind);
            Assert.Equal(QueryId, target.ObjectId);
        });

    // The pre-#4582 behaviour, kept: a name only a page answers is still a page.
    [Fact]
    public void PageName_StillResolvesToThatPage()
        => WithLoadedDependency(() =>
        {
            var target = Resolve(RunPageAction);
            Assert.Equal("Page", target.Kind);
            Assert.Equal(TargetPageId, target.ObjectId);
        });

    // A page and a codeunit share the name: the runner cannot tell which the AL named.
    [Fact]
    public void NameSharedByAPageAndACodeunit_IsRefusedNamingBothKinds()
        => WithLoadedDependency(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Resolve(RunSharedAction));
            Assert.Contains("'AROK Shared'", ex.Message);
            // Both candidates, by kind AND id, so the developer can see what collided.
            Assert.Contains("page 88245803", ex.Message);
            Assert.Contains("codeunit 88245811", ex.Message);
            Assert.Contains("#4622", ex.Message);
        });

    // Two codeunits (two ids) share the name: equally ambiguous, and must not pick the first.
    [Fact]
    public void NameSharedByTwoObjectsOfOneKind_IsRefused()
        => WithLoadedDependency(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Resolve(RunTwoCodeunitsAction));
            Assert.Contains("'AROK Twin'", ex.Message);
            Assert.Contains("codeunit 88245812", ex.Message);
            Assert.Contains("codeunit 88245813", ex.Message);
            Assert.Contains("#4622", ex.Message);
        });

    // Nothing answers the name: reported as unresolved (ObjectId 0) for the caller's refusal.
    [Fact]
    public void UnknownName_ResolvesToNothing()
        => WithLoadedDependency(() =>
        {
            var target = Resolve(RunNowhereAction);
            Assert.Equal(0, target.ObjectId);
            Assert.Equal("AROK Nowhere", target.Name);
        });
}
