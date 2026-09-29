// The runner-side half of #4974: how an action's RunPageView is read, from BC's compiled
// ActionDefinition for a page this run compiles and from SymbolReference.json for a precompiled
// one. The AL-observable claim (rows, order and filter group of the opened page) is measured
// upstream by corpus codeunits 67006 / 67007 (StefanMaron/BusinessCentral.AL.Language.Tests#508);
// these pin the reads that feed RunnerPageInstance.ApplyActionRunView.
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() and RecordPatches' dependency state resolve through the process-global
// CacheRoots override, the same reason the sibling dependency suites serialise.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ActionRunPageViewTests
{
    private const int TableId = 88249741;
    private const int HostPageId = 88249742;
    private const int TargetPageId = 88249743;
    private const int SortedViewActionId = 649740001;
    private const int NoViewActionId = 649740002;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Tables": [
            {
              "Id": 88249741,
              "Name": "ARPVT Row",
              "Fields": [
                { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" }, "Properties": [] },
                { "Id": 2, "Name": "Bucket", "TypeDefinition": { "Name": "Code[10]" }, "Properties": [] },
                { "Id": 3, "Name": "Rank", "TypeDefinition": { "Name": "Integer" }, "Properties": [] }
              ],
              "Keys": [
                { "Name": "PK", "FieldNames": [ "Entry No." ], "Properties": [] },
                { "Name": "ByRank", "FieldNames": [ "Rank" ], "Properties": [] }
              ],
              "Properties": []
            }
          ],
          "Pages": [
            {
              "Id": 88249742,
              "Name": "ARPVT Host",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "88249741" }
              ],
              "Actions": [
                {
                  "Id": 2, "Name": "processing",
                  "Actions": [
                    { "Kind": 2, "Id": 649740001, "Name": "Sorted View",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPVT Target" },
                        { "Name": "RunPageView", "Value": "sorting(Rank)\r\n  order(descending)\r\n  where(Bucket = const(KEEP), \"Entry No.\" = filter(1..3))" }
                      ] },
                    { "Kind": 2, "Id": 649740002, "Name": "No View",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPVT Target" }
                      ] }
                  ]
                }
              ]
            },
            {
              "Id": 88249743,
              "Name": "ARPVT Target",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88249741" }
              ]
            }
          ]
        }
        """;

    private static string WriteApp(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static void WithApp(Action<string> body)
    {
        var dir = TestScratch.Dir("al-runner-action-runpageview-tests");
        Directory.CreateDirectory(dir);
        try { body(WriteApp(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static Dictionary<int, BcAppSymbolCache.ActionRunObjectSymbol> RunObjects(string appPath)
        => Assert.Single(BcAppSymbolCache.Get(appPath).Pages, p => p.Id == HostPageId).MemberIdToRunObject!;

    // ViewFromSymbols reads only _pageId (for the refusal message) from the instance.
    private static RunnerPageInstance.ActionRunView? ViewFromSymbols(BcAppSymbolCache.ActionRunObjectSymbol spec)
    {
        var instance = (RunnerPageInstance)RuntimeHelpers.GetUninitializedObject(typeof(RunnerPageInstance));
        typeof(RunnerPageInstance).GetField("_pageId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(instance, HostPageId);
        return instance.ViewFromSymbols(4242, spec, TargetPageId);
    }

    [Fact]
    public void SymbolReader_ParsesTheActionsRunPageView()
        => WithApp(app =>
        {
            var view = RunObjects(app)[SortedViewActionId].RunPageView;

            Assert.NotNull(view);
            Assert.Equal(new[] { "Rank" }, view!.SortingFields.Select(f => f.FieldName));
            Assert.False(view.Ascending);
            Assert.Equal(2, view.Filters.Count);
            Assert.Equal(("Bucket", "const", "KEEP"), (view.Filters[0].FieldName, view.Filters[0].Kind, view.Filters[0].Value));
            Assert.Equal(("Entry No.", "filter", "1..3"), (view.Filters[1].FieldName, view.Filters[1].Kind, view.Filters[1].Value));
        });

    [Fact]
    public void SymbolReader_AnActionWithoutRunPageView_CarriesNone()
        => WithApp(app => Assert.Null(RunObjects(app)[NoViewActionId].RunPageView));

    [Fact]
    public void ViewFromSymbols_ResolvesFieldNumbersAndPutsWhereInFilterGroup3()
        => WithApp(app =>
        {
            RecordPatches.AddBcAppPath(app);
            var view = ViewFromSymbols(RunObjects(app)[SortedViewActionId]);

            Assert.NotNull(view);
            Assert.Equal(new[] { 3 }, view!.KeyFieldIds);
            Assert.False(view.Ascending);
            Assert.Equal(
                new[]
                {
                    new RunnerPageInstance.ActionRunViewFilter(2, FilterType.CONST, "KEEP", 3),
                    new RunnerPageInstance.ActionRunViewFilter(1, FilterType.FILTER, "1..3", 3),
                },
                view.Filters);
        });

    [Fact]
    public void ViewFromSymbols_AFieldThatDoesNotResolve_IsRefusedByName()
        => WithApp(app =>
        {
            RecordPatches.AddBcAppPath(app);
            var spec = RunObjects(app)[SortedViewActionId] with
            {
                RunPageView = new BcAppSymbolCache.PageTableViewSymbol(
                    new List<BcAppSymbolCache.PageViewSortFieldSymbol>(), null,
                    new List<BcAppSymbolCache.PageViewFilterSymbol> { new("No Such Field", "const", "X") }),
            };

            var ex = Assert.Throws<RunnerOutOfScopeException>(() => ViewFromSymbols(spec));
            Assert.Contains("TestPage action 4242", ex.Message);
            Assert.Contains("\"No Such Field\" did not resolve", ex.Message);
        });

    [Fact]
    public void ViewFromSymbols_NoRunPageView_ReturnsNull()
        => WithApp(app =>
        {
            RecordPatches.AddBcAppPath(app);
            Assert.Null(ViewFromSymbols(RunObjects(app)[NoViewActionId]));
        });

    [Fact]
    public void ViewFromMetadata_KeepsTheCompilersKeyDirectionAndFilterGroup()
    {
        var view = RunnerPageInstance.ViewFromMetadata(new ViewDefinition
        {
            Sorting = new ViewDefinitionSorting
            {
                KeyFields = "Field3,Field1", KeyFieldsSetByView = true, AscendingSetByView = true, Ascending = false,
            },
            TableFilters = new List<FilterDefinition>
            {
                new() { FieldID = 2, FilterType = FilterType.CONST, FilterValue = "KEEP", FilterGroup = 3 },
            },
        });

        Assert.NotNull(view);
        Assert.Equal(new[] { 3, 1 }, view!.KeyFieldIds);
        Assert.False(view.Ascending);
        Assert.Equal(new[] { new RunnerPageInstance.ActionRunViewFilter(2, FilterType.CONST, "KEEP", 3) }, view.Filters);
    }

    [Fact]
    public void ViewFromMetadata_NoView_ReturnsNull()
        => Assert.Null(RunnerPageInstance.ViewFromMetadata(null));
}
