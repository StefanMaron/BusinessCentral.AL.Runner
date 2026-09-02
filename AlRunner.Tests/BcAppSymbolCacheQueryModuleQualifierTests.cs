// BcAppSymbolCacheQueryModuleQualifierTests — proves BcAppSymbolCache.TryParseQueryDataItem
// strips the module qualifier off a Query dataitem's RelatedTable (issue #2295).
//
// Gap being fixed
// ----------------
// A Query dataitem bound to a table from a DIFFERENT module (a dependency application's
// table — Base Application's Item is the reported case, but the shape is any cross-module
// reference) arrives in SymbolReference.json as a module-qualified name:
// "#<appIdNoHyphens>#Item", not the plain "Item". BcAppSymbolCache.TryParseQueryDataItem used
// to preserve that string verbatim as QueryDataItemSymbol.RelatedTable.
// RecordPatches.ResolveTableIdByName then compares it against ordinary (unqualified) table
// names and never matches, so RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign abandons
// the build and the Query is constructed with NCLMetaQuery=NULL — every SetRange/Open/Read on
// it then NREs deep inside precompiled NavQuery methods (ValidateExpectedType,
// ValidateTablesNotVirtual), long before any AL-observable "table not found" error. Report
// dataitems already had this normalization (BcAppSymbolCache.StripModuleQualifier, applied by
// CollectReportDataItems); Query dataitems did not.
//
// Test strategy
// -------------
// Constructs a minimal .app (a zip with a hand-written SymbolReference.json — the same
// zip-with-raw-JSON technique BcAppSymbolCacheQueryMethodVersionTests uses) whose Query
// dataitem's RelatedTable is module-qualified, exactly the shape the compiler emits for a
// cross-module reference. Calls BcAppSymbolCache.Get() directly — no full AL compile, no Base
// Application dependency needed, so this stays within the C#-fixture "platform, never
// application" rule while still exercising the REAL parsing code path end to end. Asserts the
// parsed RelatedTable is the unqualified name, for both the root dataitem and a nested one
// (the fix lives in the one recursive TryParseQueryDataItem call, so both must normalize).
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class BcAppSymbolCacheQueryModuleQualifierTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-symbol-cache-query-module-qualifier-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteApp(string dir, string fileName, string queryName)
    {
        var appPath = Path.Combine(dir, fileName);
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            // "#437dbf0e84ff417a965ded2bb9650972#Item" — the exact qualified-reference shape
            // from the issue's own diagnostic output, on both the root dataitem AND a nested
            // one (a table from yet another module, so the two don't coincidentally match).
            w.Write($$"""
                {
                  "RuntimeVersion": "15.1",
                  "Queries": [
                    {
                      "Id": 90310,
                      "Name": "{{queryName}}",
                      "Properties": [ { "Name": "QueryType", "Value": "Normal" } ],
                      "Elements": [
                        {
                          "Id": 1,
                          "Name": "Item",
                          "RelatedTable": "#437dbf0e84ff417a965ded2bb9650972#Item",
                          "Properties": [],
                          "Columns": [
                            { "Id": 1, "Name": "No", "SourceColumn": "No.", "Properties": [] }
                          ],
                          "Filters": [],
                          "DataItems": [
                            {
                              "Id": 2,
                              "Name": "ItemLedgerEntry",
                              "RelatedTable": "#9a1b2c3d4e5f60718293a4b5c6d7e8f9#Item Ledger Entry",
                              "Properties": [ { "Name": "SqlJoinType", "Value": "InnerJoin" }, { "Name": "DataItemLink", "Value": "\"No.\" = Item.\"No.\"" } ],
                              "Columns": [
                                { "Id": 2, "Name": "EntryNo", "SourceColumn": "Entry No.", "Properties": [] }
                              ],
                              "Filters": []
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """);
        }
        return appPath;
    }

    [Fact]
    public void Get_QueryDataItemWithModuleQualifiedRelatedTable_StripsQualifierOnRootAndNested()
    {
        var dir = NewTempDir();
        try
        {
            var queryName = "IQ Item Rows " + Guid.NewGuid().ToString("N");
            var appPath = WriteApp(dir, "iq-" + Guid.NewGuid().ToString("N") + ".app", queryName);

            BcAppSymbolCache.ResetProcessCacheForTests();

            var symbols = BcAppSymbolCache.Get(appPath);

            var query = Assert.Single(symbols.Queries, q => q.Name == queryName);
            var root = Assert.Single(query.DataItems);
            var nested = Assert.Single(root.DataItems);

            // The decisive assertions: both dataitems' RelatedTable is the PLAIN name — the
            // qualifier is gone, not just tolerated somewhere downstream — which is exactly
            // what RecordPatches.ResolveTableIdByName needs to match against ordinary
            // (unqualified) table names.
            Assert.Equal("Item", root.RelatedTable);
            Assert.Equal("Item Ledger Entry", nested.RelatedTable);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Negative/control companion: an UNQUALIFIED RelatedTable (an application-local table,
    /// the shape every other Query test in this repo already exercises) must pass through
    /// unchanged — StripModuleQualifier only strips a leading module-qualifier form, and
    /// this proves the fix does not corrupt the far more common local-table case.
    /// </summary>
    [Fact]
    public void Get_QueryDataItemWithPlainRelatedTable_IsUnchanged()
    {
        var dir = NewTempDir();
        try
        {
            var queryName = "IQ Plain Rows " + Guid.NewGuid().ToString("N");
            var appPath = Path.Combine(dir, "iq-plain-" + Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            {
                var entry = za.CreateEntry("SymbolReference.json");
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write($$"""
                    {
                      "RuntimeVersion": "15.1",
                      "Queries": [
                        {
                          "Id": 90311,
                          "Name": "{{queryName}}",
                          "Properties": [ { "Name": "QueryType", "Value": "Normal" } ],
                          "Elements": [
                            {
                              "Id": 1,
                              "Name": "LocalTable",
                              "RelatedTable": "IQ Local Table",
                              "Properties": [],
                              "Columns": [ { "Id": 1, "Name": "No", "SourceColumn": "No.", "Properties": [] } ],
                              "Filters": []
                            }
                          ]
                        }
                      ]
                    }
                    """);
            }

            BcAppSymbolCache.ResetProcessCacheForTests();

            var symbols = BcAppSymbolCache.Get(appPath);

            var query = Assert.Single(symbols.Queries, q => q.Name == queryName);
            var root = Assert.Single(query.DataItems);
            Assert.Equal("IQ Local Table", root.RelatedTable);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
