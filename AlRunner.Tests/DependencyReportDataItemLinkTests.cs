// DependencyReportDataItemLinkTests — the two pieces of a PRECOMPILED report's metadata the
// runner reconstructs from SymbolReference.json that a report DATASET cannot do without.
//
// Both were invisible until #2436 made Report.Run() actually build a dataset. Before that a
// report ran with BC's NullResultSetProcessor, which discards every row and evaluates no
// column, so neither omission had an observable effect.
//
// 1. DataItemLink / DataItemLinkReference — the parent-child join.
//    DataItemIterator.SetDataItemLink resolves DataItemLinkReference against each data
//    item's DataItemVarName and hands DataItemLink to RecordDataItemLink, which parses it
//    with BC's own TableViewParser. With neither element present the nested data item has
//    no restriction at all and iterates its WHOLE table once per parent row. Measured on
//    report 411 "Vendor - Payment Receipt" against Microsoft's own ERMPurchDocReports
//    tests: 645,593 rows and a 1.45 GB dataset file, still growing when the 60s watchdog
//    aborted the suite — against 6 rows and 17 KB once the link is emitted.
//
// 2. A column's FieldType has to be a DATASET column type, not the AL type of its source
//    expression. NavDataSetBuilder.AddColumnToDataTable turns FieldType into a CLR column
//    type through CommonTypeInformation.ResolveClrType, which throws
//    "ArgumentException: UnsupportedType" for anything it has no mapping for — killing the
//    whole dataset over one column. A column whose expression is a Label arrives from the
//    symbol file as "Label", and there is no Label dataset column; report 411 alone has 23.
//
// These assert on the runner's own reconstruction, which is why they live here rather than
// in the corpus: what BC does with a request page and a dataset is pinned upstream in
// handlers/TestReportRunWithRequestPage.al.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Joins CacheRootsSerialCollection for the same reason BcAppSymbolCacheReportTests does:
// BcAppSymbolCache.Get() resolves its on-disk path through the process-global CacheRoots
// override (#1821).
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyReportDataItemLinkTests
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

    // The shape report 411 "Vendor - Payment Receipt" really has in the Base Application's
    // SymbolReference.json, trimmed to the three data items that matter: an outer record
    // item, the Integer page loop under it, and a nested item joined back to the outer one.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "Purchases",
              "Reports": [
                {
                  "Id": 411,
                  "Name": "Vendor - Payment Receipt",
                  "Properties": [
                    { "Name": "Caption", "Value": "Vendor - Payment Receipt" }
                  ],
                  "DataItems": [
                    {
                      "Id": 1,
                      "Name": "Vendor Ledger Entry",
                      "RelatedTable": "Vendor Ledger Entry",
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(\"Document Type\", \"Vendor No.\")" }
                      ],
                      "DataItems": [
                        {
                          "Id": 2,
                          "Name": "PageLoop",
                          "RelatedTable": "#8874ed3a064342479ced7a7002f7135d#Integer",
                          "Indentation": 1,
                          "Properties": [
                            { "Name": "DataItemTableView", "Value": "sorting(Number) where(Number = const(1))" }
                          ],
                          "DataItems": [
                            {
                              "Id": 3,
                              "Name": "DetailedVendorLedgEntry1",
                              "RelatedTable": "Detailed Vendor Ledg. Entry",
                              "Indentation": 2,
                              "Properties": [
                                { "Name": "DataItemLink", "Value": "\"Applied Vend. Ledger Entry No.\" = field(\"Entry No.\")" },
                                { "Name": "DataItemLinkReference", "Value": "Vendor Ledger Entry" },
                                { "Name": "PrintOnlyIfDetail", "Value": "1" }
                              ],
                              "Columns": [
                                {
                                  "Id": 10,
                                  "Name": "AppliedVLENo_DtldVendLedgEntry",
                                  "TypeDefinition": { "Name": "Integer" }
                                },
                                {
                                  "Id": 11,
                                  "Name": "PageCaptionLbl",
                                  "TypeDefinition": { "Name": "Label" }
                                },
                                {
                                  "Id": 12,
                                  "Name": "AttachmentBlob",
                                  "TypeDefinition": { "Name": "BLOB" }
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ReportDataItem_CarriesItsJoinBackToTheParent()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-dataitemlink-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var report = Assert.Single(BcAppSymbolCache.Get(appPath).Reports, r => r.Id == 411);
            var joined = Assert.Single(report.DataItems, d => d.Name == "DetailedVendorLedgEntry1");

            // The link, verbatim as the symbol file states it — DataItemIterator hands this
            // string to BC's own TableViewParser, so the AL spelling is what it wants.
            Assert.Equal(
                "\"Applied Vend. Ledger Entry No.\" = field(\"Entry No.\")",
                joined.DataItemLink);
            // The reference is the PARENT DATA ITEM's name, which is what
            // SetDataItemLink matches against DataItemVarName — not a table name.
            Assert.Equal("Vendor Ledger Entry", joined.DataItemLinkReference);
            Assert.True(joined.PrintOnlyIfDetail,
                "PrintOnlyIfDetail is stated as \"1\" and decides whether a parent row with no "
                + "detail rows appears in the dataset at all.");

            // Negative direction: an item that states no link must not acquire one. The outer
            // record item and the Integer page loop are both unjoined, and inventing a link
            // for either would filter rows the report is supposed to produce.
            var outer = Assert.Single(report.DataItems, d => d.Name == "Vendor Ledger Entry");
            Assert.Null(outer.DataItemLink);
            Assert.Null(outer.DataItemLinkReference);
            Assert.False(outer.PrintOnlyIfDetail);

            var pageLoop = Assert.Single(report.DataItems, d => d.Name == "PageLoop");
            Assert.Null(pageLoop.DataItemLink);
            Assert.Null(pageLoop.DataItemLinkReference);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportMetadataXml_StatesTheJoinAndOnlyDatasetColumnTypes()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-dataitemlink-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var report = Assert.Single(BcAppSymbolCache.Get(appPath).Reports, r => r.Id == 411);
            var xml = RecordPatches.EmitReportXml(report, sourceExprByColumn: null);

            // MetaDataItem reads these two by element name (uppercased) and nothing else
            // supplies them, so their absence is the whole defect.
            Assert.Contains(
                "<DataItemLink>\"Applied Vend. Ledger Entry No.\" = field(\"Entry No.\")</DataItemLink>",
                xml);
            Assert.Contains("<DataItemLinkReference>Vendor Ledger Entry</DataItemLinkReference>", xml);
            Assert.Contains("<PrintOnlyIfDetail>1</PrintOnlyIfDetail>", xml);

            // A column type ResolveClrType understands is written through unchanged...
            Assert.Contains("<FriendlyFieldName>AppliedVLENo_DtldVendLedgEntry</FriendlyFieldName>", xml);
            Assert.Contains("<FieldType>Integer</FieldType>", xml);
            Assert.Contains("<FieldType>BLOB</FieldType>", xml);
            // ...and one it does not (Label is an AL type, never a dataset column type)
            // becomes Text rather than reaching ResolveClrType and taking the whole
            // document down with ArgumentException: UnsupportedType.
            Assert.DoesNotContain("<FieldType>Label</FieldType>", xml);
            Assert.Contains("<FieldType>Text</FieldType>", xml);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
