// BcAppSymbolCacheTableNamespaceTests — a dependency's table carries the namespace its
// SymbolReference.json states it under (#5224).
//
// RecordPatches.ResolveInFileScope needs it: a name a bundle table writes means the table of
// that name in the writer's OWN namespace first, and an own-namespace table can be a dependency's.
// The BC half (Base Application's table beating an imported bundle table of the same name) is
// proved upstream by BusinessCentral.AL.Language.Tests codeunit 69217 "Test Relation Own NS Dep".
// This is the parser half: which Namespaces node a Tables entry sat under, kept through the
// on-disk cache, and Usings left null so a symbol-read table is still told apart from a
// source-parsed one (RecordPatches.ResolveInFileScope filters on that).
//
// The .app shape (a plain zip holding SymbolReference.json) mirrors
// BcAppSymbolCacheTableMetadataPropertiesTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() resolves its on-disk path through the process-global CacheRoots
// override (#1821), so this joins CacheRootsSerialCollection like its siblings.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCacheTableNamespaceTests
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

    // One table at the root (the global namespace), one three levels down. The same name sits in
    // two different namespaces so a reader keyed on the name alone cannot satisfy both rows.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            { "Id": 60921, "Name": "NS Shared Name",
              "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 10 } } ] }
          ],
          "Namespaces": [
            { "Name": "Microsoft",
              "Namespaces": [
                { "Name": "Foundation",
                  "Namespaces": [
                    { "Name": "Shipping",
                      "Tables": [
                        { "Id": 60922, "Name": "NS Shared Name",
                          "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 10 } } ] }
                      ] }
                  ] }
              ] },
            { "Name": "Contoso",
              "Tables": [
                { "Id": 60923, "Name": "NS Other",
                  "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Length": 10 } } ] }
              ] }
          ]
        }
        """;

    private static void AssertNamespaces(IReadOnlyList<ParsedTable> tables)
    {
        var root = Assert.Single(tables, t => t.TableId == 60921);
        Assert.Null(root.Namespace);

        var nested = Assert.Single(tables, t => t.TableId == 60922);
        Assert.Equal("Microsoft.Foundation.Shipping", nested.Namespace);
        Assert.Equal(root.TableName, nested.TableName);

        Assert.Equal("Contoso", Assert.Single(tables, t => t.TableId == 60923).Namespace);

        // Usings stays null: it is what says "parsed from AL source" to ResolveInFileScope.
        Assert.All(tables, t => Assert.Null(t.Usings));
    }

    [Fact]
    public void Tables_CarryTheNamespaceTheyWereReachedThrough_AndNoUsings()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tablens-tests");
        Directory.CreateDirectory(dir);
        try
        {
            AssertNamespaces(BcAppSymbolCache.Get(WriteApp(dir, SymbolReference)).Tables);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tables_NamespaceSurvivesTheOnDiskCache()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tablens-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var app = WriteApp(dir, SymbolReference);
            BcAppSymbolCache.Get(app);
            var before = BcAppSymbolCache.ParseInvocationCountForTests(app);

            // A fresh process: nothing in memory, so the answer comes off the disk entry.
            BcAppSymbolCache.ResetProcessCacheForTests();
            var tables = BcAppSymbolCache.Get(app).Tables;

            Assert.Equal(before, BcAppSymbolCache.ParseInvocationCountForTests(app));
            AssertNamespaces(tables);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
