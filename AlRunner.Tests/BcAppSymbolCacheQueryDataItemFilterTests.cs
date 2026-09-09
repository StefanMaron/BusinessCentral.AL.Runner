// BcAppSymbolCacheQueryDataItemFilterTests — the SymbolReference-derivation arm of #3571 and
// #3572, which is the arm a PACKAGED DEPENDENCY's query takes.
//
// Why this arm needs its own test
// -------------------------------
// #3608/PR #3617 gave a query the runner COMPILED its MetaQuery design from BC's own emitted
// metadata document. A query living in a precompiled dependency .app was never emitted here,
// so no document exists and RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign keeps the
// SymbolReference derivation — the route BcAppSymbolCache feeds. Both issues report that
// route, and neither is reachable through the document arm.
//
// What the two defects were, measured on the real compiler's own output
// ---------------------------------------------------------------------
// A dep app was compiled on BC 28.1 and its emitted SymbolReference read back verbatim. It
// states each property as ONE string:
//
//   { "Name": "DataItemTableFilter", "Value": "Status = const(Open)" }
//   { "Name": "DataItemLink",
//     "Value": "\"Header No.\" = Hdr.\"No.\", \"Variant Code\" = Hdr.\"Variant Code\"" }
//
// #3571 — QueryDataItemSymbol carried no DataItemTableFilter property at all, so the string
// above was read and discarded. The static filter never reached the design object and the
// query returned rows real BC excludes.
//
// #3572 — ParseDataItemLink split on the FIRST '=' and the FIRST '.', so the two-equality
// string above parsed to a single link whose source field was the literal
// `No.", "Variant Code" = Hdr."Variant Code`. That name resolves to no field, ParseDataItemLink
// answered null, and BuildMetaQueryDesign abandoned the whole build — leaving NCLMetaQuery
// null, which is what NavQuery.ValidateTablesNotVirtual then dereferenced.
//
// Test strategy
// -------------
// The same zip-with-hand-written-SymbolReference.json technique
// BcAppSymbolCacheQueryModuleQualifierTests and BcAppSymbolCacheQueryMethodVersionTests use:
// call BcAppSymbolCache.Get() directly, no AL compile and no Base Application dependency, so
// the real parsing code path runs end to end. The property VALUES below are copied verbatim
// from the compiler output quoted above rather than invented, so a shape the compiler does not
// actually emit cannot make these pass.
//
// These assert what the SYMBOL LAYER carries. The AL-observable half — that the filter
// actually restricts rows and that both equalities actually constrain the join — is a claim
// about BC's behaviour and is adjudicated on a real service tier by the corpus PR named in
// this change's pull request.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class BcAppSymbolCacheQueryDataItemFilterTests
{
    private static string NewTempDir()
    {
        var dir = TestScratch.FlatDir("bc-symbol-cache-query-dataitem-filter-tests-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Writes a one-query .app whose root dataitem carries <paramref name="rootProperties"/>
    /// and whose nested dataitem carries <paramref name="nestedProperties"/> — both raw
    /// SymbolReference "Properties" array bodies, so a test can state exactly the property
    /// text BC's compiler emits.
    /// </summary>
    private static string WriteApp(string dir, string queryName, string rootProperties, string nestedProperties)
    {
        var appPath = Path.Combine(dir, "qdf-" + Guid.NewGuid().ToString("N") + ".app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write($$"""
                {
                  "RuntimeVersion": "17.0",
                  "Queries": [
                    {
                      "Id": 90410,
                      "Name": "{{queryName}}",
                      "Properties": [ { "Name": "QueryType", "Value": "Normal" } ],
                      "Elements": [
                        {
                          "Id": 1,
                          "Name": "Hdr",
                          "RelatedTable": "QDF Link Header",
                          "Properties": [ {{rootProperties}} ],
                          "Columns": [
                            { "Id": 1, "Name": "HdrNo", "SourceColumn": "No.", "Properties": [] }
                          ],
                          "Filters": [],
                          "DataItems": [
                            {
                              "Id": 2,
                              "Name": "Ln",
                              "RelatedTable": "QDF Link Line",
                              "Properties": [ {{nestedProperties}} ],
                              "Columns": [
                                { "Id": 2, "Name": "LineTag", "SourceColumn": "Tag", "Properties": [] }
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

    private static BcAppSymbolCache.QuerySymbol Parse(string dir, string queryName, string rootProps, string nestedProps)
    {
        var appPath = WriteApp(dir, queryName, rootProps, nestedProps);
        BcAppSymbolCache.ResetProcessCacheForTests();
        var symbols = BcAppSymbolCache.Get(appPath);
        return Assert.Single(symbols.Queries, q => q.Name == queryName);
    }

    /// <summary>
    /// #3571 — the root dataitem's <c>DataItemTableFilter</c> reaches
    /// <c>QueryDataItemSymbol.DataItemTableFilter</c> verbatim, and a nested dataitem's does
    /// too (the property is read in the one recursive TryParseQueryDataItem call, so both
    /// levels must carry it — the issue's "static filters are preserved for root and nested
    /// data items" acceptance criterion).
    ///
    /// Asserts the concrete property text, not merely that something non-null arrived: the
    /// value is what RecordPatches.TryParseColumnFilterText later parses into a field number
    /// and a CONST/FILTER kind, so a truncated or re-spelled string would build a different
    /// filter while still being non-null.
    /// </summary>
    [Fact]
    public void Get_QueryDataItemTableFilter_IsCarriedOnRootAndNested()
    {
        var dir = NewTempDir();
        try
        {
            var name = "QDF Filter " + Guid.NewGuid().ToString("N");
            var query = Parse(dir, name,
                rootProps: """{ "Name": "DataItemTableFilter", "Value": "Status = const(Open)" }""",
                nestedProps: """{ "Name": "SqlJoinType", "Value": "InnerJoin" }, { "Name": "DataItemLink", "Value": "\"Header No.\" = Hdr.\"No.\"" }, { "Name": "DataItemTableFilter", "Value": "Tag = filter(<> ''), Blocked = const(false)" }""");

            var root = Assert.Single(query.DataItems);
            var nested = Assert.Single(root.DataItems);

            Assert.Equal("Status = const(Open)", root.DataItemTableFilter);
            // The nested one carries TWO comma-separated conditions, the issue's "multiple
            // filter fields are combined as AL declares them" criterion. It arrives as one
            // verbatim string; the split into conditions is TryParseColumnFilterText's job and
            // is quote-aware, so the `<> ''` value's own characters must survive intact here.
            Assert.Equal("Tag = filter(<> ''), Blocked = const(false)", nested.DataItemTableFilter);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Negative control for #3571: a dataitem that declares NO <c>DataItemTableFilter</c>
    /// answers null, not empty string and not a stale value from a sibling. Without this a
    /// parser that always reported some filter would satisfy the positive test above.
    /// </summary>
    [Fact]
    public void Get_QueryDataItemWithoutTableFilter_IsNull()
    {
        var dir = NewTempDir();
        try
        {
            var name = "QDF NoFilter " + Guid.NewGuid().ToString("N");
            var query = Parse(dir, name,
                rootProps: """{ "Name": "DataItemTableFilter", "Value": "Status = const(Open)" }""",
                nestedProps: """{ "Name": "SqlJoinType", "Value": "InnerJoin" }, { "Name": "DataItemLink", "Value": "\"Header No.\" = Hdr.\"No.\"" }""");

            var root = Assert.Single(query.DataItems);
            var nested = Assert.Single(root.DataItems);

            Assert.Equal("Status = const(Open)", root.DataItemTableFilter);
            Assert.Null(nested.DataItemTableFilter);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// #3572 — a two-field <c>DataItemLink</c> arrives as ONE string carrying both equalities,
    /// exactly as BC's compiler emits it. This pins the symbol layer's half: the property is
    /// carried whole, so the builder can split it on top-level commas into one
    /// MetaQueryDataItemLink per equality.
    ///
    /// The string asserted here is copied verbatim from a real BC 28.1 compilation of
    /// <c>DataItemLink = "Header No." = Hdr."No.", "Variant Code" = Hdr."Variant Code";</c>.
    /// It is the input on which the old first-'='/first-'.' parse produced a source field named
    /// <c>No.", "Variant Code" = Hdr."Variant Code</c> and abandoned the build.
    /// </summary>
    [Fact]
    public void Get_MultiFieldDataItemLink_IsCarriedWholeWithEveryEquality()
    {
        var dir = NewTempDir();
        try
        {
            var name = "QDF Composite " + Guid.NewGuid().ToString("N");
            var query = Parse(dir, name,
                rootProps: "",
                nestedProps: """{ "Name": "SqlJoinType", "Value": "InnerJoin" }, { "Name": "DataItemLink", "Value": "\"Header No.\" = Hdr.\"No.\", \"Variant Code\" = Hdr.\"Variant Code\"" }""");

            var root = Assert.Single(query.DataItems);
            var nested = Assert.Single(root.DataItems);

            Assert.Equal("\"Header No.\" = Hdr.\"No.\", \"Variant Code\" = Hdr.\"Variant Code\"", nested.DataItemLink);
            Assert.Equal("InnerJoin", nested.SqlJoinType);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
