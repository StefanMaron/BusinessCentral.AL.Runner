// BcAppSymbolCacheTableRelationReadTests — issue #4105.
//
// RUNNER-MECHANISM claim. That renaming a Base Application Location carries Item Journal
// Line."Location Code" along, and leaves Item Analysis View."Location Filter"
// (ValidateTableRelation = false) alone, is BC behaviour and is asserted upstream by corpus
// codeunit 60975 "Test Rename Prop BaseApp". What is pinned HERE is the one runner layer that
// test reaches and nothing else pins directly: BcAppSymbolCache.TryParseTableSymbol reading
// both properties off a precompiled TABLE field. BcAppSymbolCacheTableExtRelationTests pins
// the tableextension copy of that loop; this is the table loop itself.
//
// The field shapes are copied from Base Application 28.1.49838.53910's SymbolReference.json.
// FlowFilter relations are deliberately not asserted: PR #4094 changes that gate.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get resolves its on-disk path through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCacheTableRelationReadTests
{
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "Microsoft.Inventory",
              "Tables": [
                {
                  "Id": 83,
                  "Name": "Item Journal Line",
                  "Fields": [
                    {
                      "TypeDefinition": { "Name": "Code[10]" },
                      "Properties": [ { "Name": "TableRelation", "Value": "Location" } ],
                      "Id": 9,
                      "Name": "Location Code"
                    }
                  ]
                },
                {
                  "Id": 7152,
                  "Name": "Item Analysis View",
                  "Fields": [
                    {
                      "TypeDefinition": { "Name": "Code[250]" },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "Location" },
                        { "Name": "ValidateTableRelation", "Value": "0" }
                      ],
                      "Id": 10,
                      "Name": "Location Filter"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static ParsedField ReadField(int tableId, int fieldId)
    {
        var dir = TestScratch.Dir("al-runner-bcsym-table-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var fs = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);

            var table = Assert.Single(BcAppSymbolCache.Get(appPath).Tables, t => t.TableId == tableId);
            return Assert.Single(table.Fields, f => f.FieldId == fieldId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PlainTableRelation_ReachesRelationArms_AndValidates()
    {
        var field = ReadField(83, 9);

        Assert.NotNull(field.RelationArms);
        Assert.Equal("Location", Assert.Single(field.RelationArms!).TableName);
        Assert.True(field.RelationValidate);
    }

    [Fact]
    public void ValidateTableRelationZero_KeepsTheArm_ButDoesNotValidate()
    {
        var field = ReadField(7152, 10);

        // The relation stays readable; only the check (and with it rename propagation) is off.
        Assert.NotNull(field.RelationArms);
        Assert.Equal("Location", Assert.Single(field.RelationArms!).TableName);
        Assert.False(field.RelationValidate);
    }
}
