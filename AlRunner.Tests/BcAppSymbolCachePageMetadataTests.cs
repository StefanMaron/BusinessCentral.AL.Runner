// BcAppSymbolCachePageMetadataTests — proves BcAppSymbolCache reads a page's shape out of
// a dependency's SymbolReference.json for the "Page Metadata" (2000000138) and
// "Page Control Field" (2000000192) virtual tables (issues #1769 / #1779).
//
// Gap being fixed
// ---------------
// Both tables were empty, so Page Metadata.Get(<any id>) answered false and Page Control
// Field answered no rows for every page, including Base Application ones. Base App
// "Page Management".GetDefaultCardPageID reads a table's LOOKUP page's CardPageID column
// off Page Metadata — see RecordPatches.PageMetadataVirtualTable.cs for the verified,
// real algorithm (not a SourceTable+PageType scan, which was an earlier, wrong guess).
//
// For a page the runner COMPILES, the shape comes from its AL source. For a page in a
// PRECOMPILED dependency there is no other route than this: an R2R .app ships no metadata
// XML. The shapes below mirror what Base Application 28.1's own SymbolReference.json
// states for "Customer Card" (Id 21, Card, SourceTable 18) and "Customer List" (Id 22,
// List, SourceTable 18, CardPageID = "Customer Card" — stated BY NAME, not by id) —
// captured directly from the .app, not invented.
//
// The .app shape below (a plain zip holding SymbolReference.json) mirrors
// BcAppSymbolCacheReportTests / BcAppSymbolCacheTableExtTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() now resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection to avoid racing
// CacheRootsTests's SetOverride calls — see that collection's header for why.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCachePageMetadataTests
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

    // Card page: plain field controls, one with Visible driven by a global variable name
    // (not a literal) — exactly how Base Application 28.1's Customer Card states its
    // "No." control's Visible property in the real .app.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 21,
              "Name": "Customer Card",
              "Properties": [
                { "Name": "Caption", "Value": "Customer Card" },
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "18" }
              ],
              "Controls": [
                {
                  "Kind": 1,
                  "Controls": [
                    {
                      "Kind": 8,
                      "Id": 640644145,
                      "Name": "No.",
                      "Properties": [
                        { "Name": "Visible", "Value": "NoFieldVisible" },
                        { "Name": "SourceExpression", "Value": "Rec.\"No.\"" }
                      ]
                    },
                    {
                      "Kind": 8,
                      "Id": 1165569367,
                      "Name": "Name",
                      "Properties": [
                        { "Name": "SourceExpression", "Value": "Rec.Name" }
                      ]
                    },
                    {
                      "Kind": 8,
                      "Id": 1143098565,
                      "Name": "Name 2",
                      "Properties": [
                        { "Name": "Visible", "Value": "false" },
                        { "Name": "SourceExpression", "Value": "Rec.\"Name 2\"" }
                      ]
                    }
                  ]
                }
              ]
            },
            {
              "Id": 22,
              "Name": "Customer List",
              "Properties": [
                { "Name": "Caption", "Value": "Customers" },
                { "Name": "CardPageID", "Value": "Customer Card" },
                { "Name": "Editable", "Value": "0" },
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "18" }
              ]
            },
            {
              "Id": 23,
              "Name": "Bare Page",
              "Properties": []
            }
          ]
        }
        """;

    [Fact]
    public void Pages_CardPage_HasPageTypeCaptionSourceTableAndOrderedControls()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var pages = BcAppSymbolCache.Get(appPath).Pages;

            var card = Assert.Single(pages, p => p.Id == 21);
            Assert.Equal("Customer Card", card.Name);
            Assert.Equal("Customer Card", card.Caption);
            Assert.Equal("Card", card.PageType);
            Assert.Equal(18, card.SourceTableId);
            // AL defaults: a page with no CardPageId/Editable properties states neither —
            // Editable stays true (see the sibling "Bare Page" test), CardPageName is null
            // (a Card page normally declares none; nothing here should invent one).
            Assert.Null(card.CardPageName);

            Assert.NotNull(card.Controls);
            Assert.Equal(3, card.Controls!.Count);

            // Sequence follows document order, 1-based, depth-first through the group.
            var no = card.Controls[0];
            Assert.Equal(640644145, no.Id);
            Assert.Equal("No.", no.Name);
            Assert.Equal("Rec.\"No.\"", no.SourceExpression);
            // A Visible driven by a variable name is stored VERBATIM — not coerced to a
            // boolean, not dropped. Real BC's own column is Text for exactly this reason.
            Assert.Equal("NoFieldVisible", no.VisibleExpr);
            Assert.Equal(1, no.Sequence);

            var name = card.Controls[1];
            Assert.Equal("Name", name.Name);
            Assert.Equal("Rec.Name", name.SourceExpression);
            // No Visible property declared at all — the symbol file states nothing, and
            // the parser must not invent a value; the provider (not this parser) supplies
            // the "true" default. See RecordPatches.PageControlFieldVirtualTable.cs.
            Assert.Null(name.VisibleExpr);
            Assert.Equal(2, name.Sequence);

            var name2 = card.Controls[2];
            Assert.Equal("Name 2", name2.Name);
            Assert.Equal("false", name2.VisibleExpr);
            Assert.Equal(3, name2.Sequence);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pages_ListPage_CardPageIdIsStatedByName_NotById()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var pages = BcAppSymbolCache.Get(appPath).Pages;

            var list = Assert.Single(pages, p => p.Id == 22);
            Assert.Equal("List", list.PageType);
            Assert.Equal("Customers", list.Caption);   // Caption property, different from Name
            Assert.Equal(18, list.SourceTableId);
            // The raw name, unresolved — RecordPatches.PageMetadataVirtualTable.cs resolves
            // it against the run's page inventory. A parser that resolved it to an id (or
            // left it blank) here would break that later resolution.
            Assert.Equal("Customer Card", list.CardPageName);
            // Explicit "0" → AL's Editable flips to false, only because it is stated.
            Assert.False(list.Editable);
            Assert.Empty(list.Controls!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pages_NoPropertiesDeclared_AllDefaultsApply()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var pages = BcAppSymbolCache.Get(appPath).Pages;

            var bare = Assert.Single(pages, p => p.Id == 23);
            // MS docs default: PageType = Card when the property is absent.
            Assert.Equal("Card", bare.PageType);
            Assert.Equal(0, bare.SourceTableId);
            Assert.True(bare.Editable);
            Assert.True(bare.InsertAllowed);
            Assert.True(bare.ModifyAllowed);
            Assert.True(bare.DeleteAllowed);
            Assert.False(bare.SourceTableTemporary);
            Assert.Null(bare.CardPageName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Negative: a symbol file with no Pages container at all must yield no pages — not a
    // fabricated entry, and not a crash.
    [Fact]
    public void Pages_AreEmpty_WhenTheSymbolFileDeclaresNone()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, """
                { "RuntimeVersion": "15.1", "Namespaces": [], "Tables": [] }
                """);

            Assert.Empty(BcAppSymbolCache.Get(appPath).Pages);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
