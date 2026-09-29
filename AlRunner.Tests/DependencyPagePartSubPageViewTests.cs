// DependencyPagePartSubPageViewTests — #4968: a subpage part's SubPageView on a page reconstructed
// from a precompiled dependency's SymbolReference.json reaches the part as BC's <SubFormView>,
// the element MockTestPage reads for a part of either origin. The BC-observable claim (a Base
// Application host's part shows only the rows its view selects) is pinned upstream in the corpus;
// this file pins the runner's own reader and emitter underneath it.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyPagePartSubPageViewTests : IDisposable
{
    private const int HostPageId = 88129001;

    private const int ViewPartId = 88129101;
    private const int WhereOnlyPartId = 88129102;
    private const int UnresolvablePartId = 88129103;
    private const int PlainPartId = 88129104;
    private const int OrderOnlyPartId = 88129105;

    // The HOST table declares "Bucket" and "Qty" too, under different ids (3, 4) than the part
    // table (8, 10): a view resolved against the host's table instead of the part's would carry
    // 3/4 and fail the id assertions below rather than pass by coincidence.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            {
              "Id": 88129020,
              "Name": "SVX Host",
              "Fields": [ { "Id": 1, "Name": "Code" }, { "Id": 3, "Name": "Bucket" }, { "Id": 4, "Name": "Qty" } ]
            },
            {
              "Id": 88129021,
              "Name": "SVX Line",
              "Fields": [
                { "Id": 7, "Name": "Line No." },
                { "Id": 8, "Name": "Bucket" },
                { "Id": 9, "Name": "Code" },
                { "Id": 10, "Name": "Qty" }
              ]
            }
          ],
          "Pages": [
            {
              "Id": 88129001,
              "Name": "SVX Host Page",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "88129020" }
              ],
              "Controls": [
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88129002 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Code\" = field(\"Code\")" },
                    { "Name": "SubPageView", "Value": "sorting(Bucket, \"Line No.\")\r\n                              order(descending)\r\n                              where(Bucket = const('KEEP'), Qty = filter(>5))" }
                  ],
                  "Id": 88129101,
                  "Name": "ViewPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88129002 },
                  "Properties": [ { "Name": "SubPageView", "Value": "where(Bucket = const('X'))" } ],
                  "Id": 88129102,
                  "Name": "WhereOnlyPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88129002 },
                  "Properties": [ { "Name": "SubPageView", "Value": "where(\"No Such Field\" = const(1))" } ],
                  "Id": 88129103,
                  "Name": "UnresolvablePart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88129002 },
                  "Id": 88129104,
                  "Name": "PlainPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88129002 },
                  "Properties": [ { "Name": "SubPageView", "Value": "order(descending)" } ],
                  "Id": 88129105,
                  "Name": "OrderOnlyPart"
                }
              ]
            },
            {
              "Id": 88129002,
              "Name": "SVX Line Part",
              "Properties": [
                { "Name": "PageType", "Value": "ListPart" },
                { "Name": "SourceTable", "Value": "88129021" }
              ]
            }
          ]
        }
        """;

    private readonly string _dir;

    public DependencyPagePartSubPageViewTests()
    {
        _dir = TestScratch.Dir("al-runner-dep-page-part-subpageview-tests");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteApp()
    {
        var appPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static (XmlElement part, XmlNamespaceManager ns) Part(int partId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(HostPageId);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        var part = (XmlElement?)doc.DocumentElement!.SelectSingleNode(
            $"m:Content/m:Containers/m:Controls[@ID='{partId}']", ns);
        Assert.NotNull(part);
        return (part!, ns);
    }

    private (XmlElement part, XmlNamespaceManager ns) LoadedPart(int partId)
    {
        RecordPatches.AddBcAppPath(WriteApp());
        return Part(partId);
    }

    [Fact]
    public void ViewPart_WhereEntries_BecomeGroup4TableFilters_OnThePartsOwnFieldIds()
    {
        var (part, ns) = LoadedPart(ViewPartId);
        var filters = part.SelectNodes("m:SubFormView/m:TableFilters", ns)!.Cast<XmlElement>().ToList();
        Assert.Equal(2, filters.Count);

        Assert.Equal("4", filters[0].GetAttribute("FilterGroup"));
        Assert.Equal("8", filters[0].GetAttribute("FieldID"));
        Assert.Equal("CONST", filters[0].GetAttribute("FilterType"));
        Assert.Equal("KEEP", filters[0].GetAttribute("FilterValue"));

        Assert.Equal("4", filters[1].GetAttribute("FilterGroup"));
        Assert.Equal("10", filters[1].GetAttribute("FieldID"));
        Assert.Equal("FILTER", filters[1].GetAttribute("FilterType"));
        Assert.Equal(">5", filters[1].GetAttribute("FilterValue"));
    }

    [Fact]
    public void ViewPart_SortingAndOrder_BecomeTheViewsKeyAndDirection()
    {
        var (part, ns) = LoadedPart(ViewPartId);
        var sorting = (XmlElement?)part.SelectSingleNode("m:SubFormView/m:Sorting", ns);
        Assert.NotNull(sorting);
        Assert.Equal("Field8,Field7", sorting!.GetAttribute("KeyFields"));
        Assert.Equal("1", sorting.GetAttribute("KeyFieldsSetByView"));
        Assert.Equal("1", sorting.GetAttribute("AscendingSetByView"));
        Assert.Equal("0", sorting.GetAttribute("Ascending"));
    }

    [Fact]
    public void ViewPart_KeepsItsSubPageLink_BesideTheView()
    {
        var (part, ns) = LoadedPart(ViewPartId);
        var link = (XmlElement?)part.SelectSingleNode("m:SubFormLink", ns);
        Assert.NotNull(link);
        Assert.Equal("9", link!.GetAttribute("FieldID"));
        Assert.Equal("FIELD", link.GetAttribute("FilterType"));
        Assert.Equal("1", link.GetAttribute("FilterValue"));
    }

    [Fact]
    public void WhereOnlyPart_CarriesAnEmptySorting_SoNoKeyIsImposed()
    {
        var (part, ns) = LoadedPart(WhereOnlyPartId);
        var sorting = (XmlElement)part.SelectSingleNode("m:SubFormView/m:Sorting", ns)!;
        Assert.Equal("0", sorting.GetAttribute("KeyFieldsSetByView"));
        Assert.Equal("0", sorting.GetAttribute("AscendingSetByView"));
        var filter = (XmlElement)part.SelectSingleNode("m:SubFormView/m:TableFilters", ns)!;
        Assert.Equal("8", filter.GetAttribute("FieldID"));
        Assert.Equal("X", filter.GetAttribute("FilterValue"));
    }

    [Fact]
    public void OrderOnlyPart_SetsTheDirectionAndNoKey()
    {
        var (part, ns) = LoadedPart(OrderOnlyPartId);
        var sorting = (XmlElement)part.SelectSingleNode("m:SubFormView/m:Sorting", ns)!;
        Assert.Equal("0", sorting.GetAttribute("KeyFieldsSetByView"));
        Assert.Equal("1", sorting.GetAttribute("AscendingSetByView"));
        Assert.Equal("0", sorting.GetAttribute("Ascending"));
        Assert.Null(part.SelectSingleNode("m:SubFormView/m:TableFilters", ns));
    }

    // A view field this run cannot resolve is written as FieldID 0, which
    // MockTestPage's SubPageView loop refuses by name rather than showing the rows it excludes.
    [Fact]
    public void UnresolvablePart_WritesFieldIdZero_SoThePartRefusesRatherThanWidens()
    {
        var (part, ns) = LoadedPart(UnresolvablePartId);
        var filter = (XmlElement)part.SelectSingleNode("m:SubFormView/m:TableFilters", ns)!;
        Assert.Equal("0", filter.GetAttribute("FieldID"));
        Assert.Equal("4", filter.GetAttribute("FilterGroup"));
    }

    [Fact]
    public void PlainPart_DeclaringNoView_CarriesNoSubFormView()
    {
        var (part, ns) = LoadedPart(PlainPartId);
        Assert.Null(part.SelectSingleNode("m:SubFormView", ns));
    }

    // The view lives in BcAppSymbolCache's on-disk payload, so a warm hit must replay it.
    [Fact]
    public void ViewPart_SurvivesAWarmSymbolCacheHit()
    {
        var appPath = WriteApp();
        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal(2, CountViewFilters());

        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        var parsesBefore = BcAppSymbolCache.ParseInvocationCountForTests(appPath);
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal(2, CountViewFilters());
        Assert.Equal(parsesBefore, BcAppSymbolCache.ParseInvocationCountForTests(appPath));

        RecordPatches.ResetForReload();
        BcAppSymbolCache.ResetProcessCacheForTests();

        static int CountViewFilters()
        {
            var (part, ns) = Part(ViewPartId);
            return part.SelectNodes("m:SubFormView/m:TableFilters", ns)!.Count;
        }
    }
}
