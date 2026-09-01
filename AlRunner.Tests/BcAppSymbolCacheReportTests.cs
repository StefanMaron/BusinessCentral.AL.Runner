// BcAppSymbolCacheReportTests — proves BcAppSymbolCache reads report shape out of a
// dependency's SymbolReference.json.
//
// Gap being fixed
// ---------------
// The Report Metadata (2000000139) and Report Data Items (2000000203) system virtual
// tables were empty, so Report Metadata.Get(<any id>) answered false and Report Data
// Items answered nothing — for every report, including Base Application ones. AL that
// discovers a report's shape through those tables (the documented way to do it) silently
// took its not-found branch: Pageworks' DeriveSourceTable returned 0 for report 1306,
// whose root data item is plainly Sales Invoice Header.
//
// For a report the runner COMPILES, the shape comes from its AL source. For a report in a
// PRECOMPILED dependency there is no other route than this: an R2R .app ships no metadata
// XML, and parsing its ~8000-file embedded src/ for this would be absurd. The symbol file
// carries Id, Name, the Caption property and the full DataItems tree with per-item
// RelatedTable and Indentation — this test pins that they are read verbatim, that AL
// property defaults are applied where the symbol file states nothing, and that nesting is
// not flattened.
//
// The .app shape below (a plain zip holding SymbolReference.json) mirrors
// BcAppSymbolCacheTableExtTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() now resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection to avoid racing
// CacheRootsTests's SetOverride calls — see that collection's header for why.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCacheReportTests
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

    // Mirrors the real Base Application shape for report 1306: reports live inside a
    // Namespaces entry (not at the root), the root data item declares no Indentation at
    // all, and the nested one carries an explicit Indentation of 1.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "Sales",
              "Reports": [
                {
                  "Id": 1306,
                  "Name": "Standard Sales - Invoice",
                  "Properties": [
                    { "Name": "Caption", "Value": "Sales - Invoice" },
                    { "Name": "WordMergeDataItem", "Value": "Header" }
                  ],
                  "DataItems": [
                    {
                      "Id": 1240373886,
                      "Name": "Header",
                      "RelatedTable": "Sales Invoice Header",
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(\"No.\")" },
                        { "Name": "RequestFilterFields", "Value": "Field3,Field2" }
                      ],
                      "DataItems": [
                        {
                          "Id": 411976133,
                          "Name": "Line",
                          "RelatedTable": "Sales Invoice Line",
                          "Indentation": 1,
                          "OwningDataItemName": "Header",
                          "Properties": [
                            { "Name": "DataItemTableView", "Value": "sorting(\"Document No.\") where(\"Entry Type\" = filter(<> \"Initial Entry\"))" }
                          ]
                        },
                        {
                          "Id": 55501,
                          "Name": "Counter",
                          "RelatedTable": "#8874ed3a064342479ced7a7002f7135d#Integer",
                          "Indentation": 1
                        }
                      ]
                    }
                  ]
                },
                {
                  "Id": 795,
                  "Name": "Adjust Cost - Item Entries",
                  "Properties": [
                    { "Name": "Caption", "Value": "Adjust Cost - Item Entries" },
                    { "Name": "ProcessingOnly", "Value": "1" },
                    { "Name": "UseRequestPage", "Value": "0" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Reports_AreReadFromANestedNamespace_WithCaptionAndDataItemTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var reports = BcAppSymbolCache.Get(appPath).Reports;

            var invoice = Assert.Single(reports, r => r.Id == 1306);
            Assert.Equal("Standard Sales - Invoice", invoice.Name);
            // Caption is the PROPERTY, deliberately different from the object name.
            Assert.Equal("Sales - Invoice", invoice.Caption);
            Assert.Equal("Header", invoice.WordMergeDataItem);

            // All three data items, flattened in declaration order, each with its OWN
            // table — a parser that only read the root, or echoed the root's table,
            // fails here.
            Assert.Equal(3, invoice.DataItems.Count);

            var root = invoice.DataItems[0];
            Assert.Equal("Header", root.Name);
            Assert.Equal("Sales Invoice Header", root.RelatedTable);
            Assert.Equal(0, root.Indentation);
            Assert.Equal(1240373886, root.Id);
            Assert.Equal("sorting(\"No.\")", root.DataItemTableView);
            Assert.Equal("Field3,Field2", root.RequestFilterFields);

            var nested = invoice.DataItems[1];
            Assert.Equal("Line", nested.Name);
            Assert.Equal("Sales Invoice Line", nested.RelatedTable);
            Assert.Equal(1, nested.Indentation);
            Assert.Equal(411976133, nested.Id);

            // #2305 — BC applies DataItemTableView with NavRecord.ALSetView, and
            // TableViewParser reads a FILTER(...) body with ' as its quote character while
            // reading SORTING field names and a field name in WHERE(...) with ". So the AL
            // spelling of a member name has to be re-quoted on the filter side ONLY: left as
            // written it reaches the option evaluator as a literal with double quotes in it
            // and Report 321 "Vendor - Balance to Date" throws instead of running.
            Assert.Equal(
                """sorting("Document No.") where("Entry Type" = filter(<> 'Initial Entry'))""",
                nested.DataItemTableView);

            // A data item bound to a table in ANOTHER module arrives module-qualified
            // (#appIdNoHyphens#Name) — most Base Application reports iterate System's
            // Integer this way. The qualifier must be stripped, or the table cannot be
            // resolved and the whole report silently disappears from both virtual tables.
            var crossModule = invoice.DataItems[2];
            Assert.Equal("Counter", crossModule.Name);
            Assert.Equal("Integer", crossModule.RelatedTable);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reports_ApplyAlPropertyDefaults_AndStatedOverrides()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var reports = BcAppSymbolCache.Get(appPath).Reports;

            // Symbol file states neither property → AL's own defaults.
            var invoice = Assert.Single(reports, r => r.Id == 1306);
            Assert.False(invoice.ProcessingOnly);
            Assert.True(invoice.UseRequestPage);

            // Symbol file states both → the stated values win, and no data items exist,
            // which is what makes FirstDataItemTableID legitimately 0 for this report.
            var adjustCost = Assert.Single(reports, r => r.Id == 795);
            Assert.True(adjustCost.ProcessingOnly);
            Assert.False(adjustCost.UseRequestPage);
            Assert.Empty(adjustCost.DataItems);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Negative: a symbol file with no Reports container at all must yield no reports —
    // not a fabricated entry, and not a crash. Every dependency that ships only tables
    // (and there are many) takes this path.
    [Fact]
    public void Reports_AreEmpty_WhenTheSymbolFileDeclaresNone()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, """
                { "RuntimeVersion": "15.1", "Namespaces": [], "Tables": [] }
                """);

            Assert.Empty(BcAppSymbolCache.Get(appPath).Reports);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
