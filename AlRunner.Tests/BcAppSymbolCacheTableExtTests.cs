// BcAppSymbolCacheTableExtTests — proves that BcAppSymbolCache.GetTableExtensions parses
// TableExtensions from a .app's SymbolReference.json.
//
// Gap being fixed
// ---------------
// BcAppSymbolCache previously had no TableExtensions support. Precompiled dependency apps
// (e.g. BC Base Application's tableextension 243 "SourceCodeSetupExt" extending
// "Source Code Setup") therefore never reached _parsedExtensionFields, causing:
//   [RecordPatches] extension field 3 from extension 243 not found in NCLMetaTable Source Code Setup
//
// Design note
// -----------
// TableExtensions are stored in a SEPARATE cache (TableExtensionCache) rather than adding a
// 4th field to AppSymbols, because changing the AppSymbols record signature causes SIGSEGV
// in R2R-compiled BC code (JIT token-shift; see docs on precompiled-dll-respect.md).
// The new GetTableExtensions(appPath) method accesses this separate cache.
//
// Test strategy
// -------------
// Build a minimal .app (plain zip) containing a hand-crafted SymbolReference.json with
// a single TableExtensions entry that mirrors the real BC SymbolReference.json shape.
// Assert the parsed result carries the expected ExtensionId, TargetTableName, and Fields.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.GetTableExtensions() now resolves its on-disk path through the
// process-global CacheRoots override, so this joins CacheRootsSerialCollection to avoid
// racing CacheRootsTests's SetOverride calls — see that collection's header for why.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCacheTableExtTests
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

    [Fact]
    public void GetTableExtensions_RootLevelEntry_ReturnsParsedExtension()
    {
        // ── ARRANGE ──────────────────────────────────────────────────────────────────
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sr = """
                {
                  "RuntimeVersion": "15.1",
                  "Namespaces": [],
                  "TableExtensions": [
                    {
                      "TargetObject": "#f3552374a1f24356848e196002525837#Source Code Setup",
                      "Fields": [
                        {
                          "TypeDefinition": { "Name": "Code[10]" },
                          "Properties": [
                            { "Name": "Caption", "Value": "Sales" }
                          ],
                          "Id": 2,
                          "Name": "Sales"
                        },
                        {
                          "TypeDefinition": { "Name": "Code[10]" },
                          "Properties": [
                            { "Name": "Caption", "Value": "Purchases" }
                          ],
                          "Id": 3,
                          "Name": "Purchases"
                        }
                      ],
                      "Id": 243,
                      "Name": "SourceCodeSetupExt"
                    }
                  ]
                }
                """;

            var appPath = WriteApp(dir, sr);

            // ── ACT ───────────────────────────────────────────────────────────────────
            var exts = BcAppSymbolCache.GetTableExtensions(appPath);

            // ── ASSERT ────────────────────────────────────────────────────────────────
            Assert.NotEmpty(exts);
            var ext = Assert.Single(exts);
            Assert.Equal(243, ext.ExtensionId);
            Assert.Equal("SourceCodeSetupExt", ext.ExtensionName);
            Assert.Equal("Source Code Setup", ext.TargetTableName);
            Assert.Equal(2, ext.Fields.Count);

            var f2 = ext.Fields.First(f => f.FieldId == 2);
            Assert.Equal("Sales", f2.FieldName);
            Assert.Equal("Code[10]", f2.TypeName);

            var f3 = ext.Fields.First(f => f.FieldId == 3);
            Assert.Equal("Purchases", f3.FieldName);
            Assert.Equal("Code[10]", f3.TypeName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetTableExtensions_NestedNamespace_ReturnsParsedExtension()
    {
        // Extension nested two namespaces deep (mirrors real BC BaseApp structure)
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sr = """
                {
                  "RuntimeVersion": "15.1",
                  "Namespaces": [
                    {
                      "Name": "Microsoft",
                      "Namespaces": [
                        {
                          "Name": "Foundation",
                          "TableExtensions": [
                            {
                              "TargetObject": "#f3552374a1f24356848e196002525837#Source Code Setup",
                              "Fields": [
                                {
                                  "TypeDefinition": { "Name": "Code[10]" },
                                  "Properties": [],
                                  "Id": 2,
                                  "Name": "Sales"
                                }
                              ],
                              "Id": 243,
                              "Name": "SourceCodeSetupExt"
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """;

            var appPath = WriteApp(dir, sr);

            // ── ACT ───────────────────────────────────────────────────────────────────
            var exts = BcAppSymbolCache.GetTableExtensions(appPath);

            // ── ASSERT ────────────────────────────────────────────────────────────────
            Assert.NotEmpty(exts);
            var ext = Assert.Single(exts);
            Assert.Equal(243, ext.ExtensionId);
            Assert.Equal("Source Code Setup", ext.TargetTableName);
            Assert.Single(ext.Fields);
            Assert.Equal(2, ext.Fields[0].FieldId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetTableExtensions_PlainTargetObjectName_StripsPrefixCorrectly()
    {
        // Some apps use a plain table name without the #appId# prefix
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sr = """
                {
                  "RuntimeVersion": "15.1",
                  "Namespaces": [],
                  "TableExtensions": [
                    {
                      "TargetObject": "Gen. Journal Line",
                      "Fields": [
                        {
                          "TypeDefinition": { "Name": "Code[20]" },
                          "Properties": [],
                          "Id": 74300,
                          "Name": "TemplateBatchNameRSABG"
                        }
                      ],
                      "Id": 74300,
                      "Name": "GenJournalLineRSABG"
                    }
                  ]
                }
                """;

            var appPath = WriteApp(dir, sr);

            // ── ACT ───────────────────────────────────────────────────────────────────
            var exts = BcAppSymbolCache.GetTableExtensions(appPath);

            // ── ASSERT ────────────────────────────────────────────────────────────────
            Assert.NotEmpty(exts);
            var ext = Assert.Single(exts);
            Assert.Equal(74300, ext.ExtensionId);
            Assert.Equal("GenJournalLineRSABG", ext.ExtensionName);
            Assert.Equal("Gen. Journal Line", ext.TargetTableName);
            Assert.Single(ext.Fields);
            Assert.Equal(74300, ext.Fields[0].FieldId);
            Assert.Equal("TemplateBatchNameRSABG", ext.Fields[0].FieldName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
