// DependencyReportMaxIterationTests — a precompiled report data item's MaxIteration, the
// third property in the same family as #2436's DataItemLink and PrintOnlyIfDetail.
//
// BC's DataItemIterator bounds a data-item loop with
//     if (dataItem.MetaData.MaxIteration != 0 && maxIteration == dataItem.MetaData.MaxIteration)
//         break;
// so 0 means NO LIMIT, not "zero iterations". A dataitem declared `MaxIteration = 1` over the
// Integer virtual table therefore runs once with the property, and without it runs to that
// table's end — every Number in [-1000000000..1000000000], BC's own clamp, since #3485 served
// that table from BC's computed provider. That is why the omission is a hang rather than a
// wrong number: see docs/limitations.md and issue #3370.
//
// Types.dll's MetaDataItem(XmlNode, ...) reads the element name uppercased, `MAXITERATION`,
// through int.Parse — the same switch that yields PRINTONLYIFDETAIL — so emitting the element
// is the entire wiring. Nothing downstream needs to change.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Joins CacheRootsSerialCollection for the same reason DependencyReportDataItemLinkTests does:
// BcAppSymbolCache.Get() resolves its on-disk path through the process-global CacheRoots
// override (#1821).
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyReportMaxIterationTests
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

    // The shape report 20 "Calc. and Post VAT Settlement" really has in Base Application
    // 28.1's SymbolReference.json — read out of the .app, not invented. "Close VAT Entries"
    // states MaxIteration = 1; its sibling "VAT Entry" states none. A third data item states
    // MaxIteration = 3 so the assertions below cannot be satisfied by hardcoding 1.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "Finance",
              "Reports": [
                {
                  "Id": 20,
                  "Name": "Calc. and Post VAT Settlement",
                  "Properties": [
                    { "Name": "Caption", "Value": "Calc. and Post VAT Settlement" }
                  ],
                  "DataItems": [
                    {
                      "Id": 1,
                      "Name": "VAT Posting Setup",
                      "RelatedTable": "VAT Posting Setup",
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(\"VAT Bus. Posting Group\", \"VAT Prod. Posting Group\")" }
                      ],
                      "DataItems": [
                        {
                          "Id": 2,
                          "Name": "Closing G/L and VAT Entry",
                          "RelatedTable": "#8874ed3a064342479ced7a7002f7135d#Integer",
                          "Indentation": 1,
                          "Properties": [
                            { "Name": "DataItemTableView", "Value": "sorting(Number)" }
                          ],
                          "DataItems": [
                            {
                              "Id": 3,
                              "Name": "VAT Entry",
                              "RelatedTable": "VAT Entry",
                              "Indentation": 2,
                              "Properties": [
                                { "Name": "DataItemTableView", "Value": "sorting(Type, Closed) where(Closed = const(false))" }
                              ]
                            },
                            {
                              "Id": 4,
                              "Name": "Close VAT Entries",
                              "RelatedTable": "#8874ed3a064342479ced7a7002f7135d#Integer",
                              "Indentation": 2,
                              "Properties": [
                                { "Name": "DataItemTableView", "Value": "sorting(Number)" },
                                { "Name": "MaxIteration", "Value": "1" }
                              ]
                            },
                            {
                              "Id": 5,
                              "Name": "ThreeTimesLoop",
                              "RelatedTable": "#8874ed3a064342479ced7a7002f7135d#Integer",
                              "Indentation": 2,
                              "Properties": [
                                { "Name": "DataItemTableView", "Value": "sorting(Number)" },
                                { "Name": "MaxIteration", "Value": "3" }
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
    public void ReportDataItem_CarriesTheDeclaredMaxIteration()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-maxiteration-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var report = Assert.Single(BcAppSymbolCache.Get(appPath).Reports, r => r.Id == 20);

            // The declared bound, verbatim. This is the data item whose omission hangs
            // Codeunit134008.CalcPostVATSettlementForSalesTax.
            var bounded = Assert.Single(report.DataItems, d => d.Name == "Close VAT Entries");
            Assert.Equal(1, bounded.MaxIteration);

            // A DIFFERENT declared value, so nothing here passes by hardcoding 1. If the
            // parse ignored the stated value and clamped every data item to one iteration,
            // this is the assertion that catches it.
            var three = Assert.Single(report.DataItems, d => d.Name == "ThreeTimesLoop");
            Assert.Equal(3, three.MaxIteration);

            // Negative direction: a data item that states no MaxIteration must stay at 0,
            // which is BC's "no limit". Inventing a bound for one of these would silently
            // truncate a report that is supposed to iterate its whole filtered set — the
            // opposite defect, and a wrong dataset rather than a hang.
            var unbounded = Assert.Single(report.DataItems, d => d.Name == "VAT Entry");
            Assert.Equal(0, unbounded.MaxIteration);

            var outer = Assert.Single(report.DataItems, d => d.Name == "VAT Posting Setup");
            Assert.Equal(0, outer.MaxIteration);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportMetadataXml_StatesMaxIterationOnlyWhereDeclared()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-maxiteration-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var report = Assert.Single(BcAppSymbolCache.Get(appPath).Reports, r => r.Id == 20);
            var xml = RecordPatches.EmitReportXml(report, sourceExprByColumn: null);

            // MetaDataItem reads MAXITERATION by element name and nothing else supplies it,
            // so its absence from this document is the whole defect.
            Assert.Contains("<MaxIteration>1</MaxIteration>", xml);
            Assert.Contains("<MaxIteration>3</MaxIteration>", xml);

            // Exactly two data items declare one, so exactly two elements may appear. A
            // blanket emission would satisfy the two Contains above and still be wrong.
            Assert.Equal(2, CountOccurrences(xml, "<MaxIteration>"));

            // And the two that declare none must carry no element at all: writing
            // <MaxIteration>0</MaxIteration> would be harmless to BC (0 is its own default)
            // but would misstate the metadata, so assert the honest shape.
            Assert.DoesNotContain("<MaxIteration>0</MaxIteration>", xml);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
