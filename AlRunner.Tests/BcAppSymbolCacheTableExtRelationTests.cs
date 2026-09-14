// BcAppSymbolCacheTableExtRelationTests — issue #3177.
//
// RUNNER-MECHANISM claim. What BC does with a TableRelation is plain BC behaviour and is
// asserted upstream in the al-language corpus against a real service tier (see the PR that
// carries this file); what is pinned HERE is the runner's own symbol reader, because the defect
// was that ONE CLASS, reading ONE package, disagreed with ITSELF about the same property:
// BcAppSymbolCache.TryParseTableSymbol re-parses a precompiled TABLE field's TableRelation
// (#2528, #2518), and BcAppSymbolCache.TryParseTableExtensionSymbol — an intentional copy of
// that loop, kept a copy for the token-shift reason in the file header — never got the change.
//
// 261 fields contributed by tableextensions in the BC 28.4 platform packages carry a
// TableRelation (154 in 28.1, the count #3177 was filed with), so FieldRef.Relation answered 0
// for all of them and Validate() accepted a value with no matching related row. #2528 recorded
// what that is: a wrong ANSWER, not a missing feature.
//
// FieldWithoutTableRelation_... is a guard ("invent a relation where the symbol declares none");
// the two FlowFilter/FlowField tests are #2789's RED -> GREEN on the extension and table loops.
//
// These assert on the parsed symbol rather than on runtime behaviour deliberately. The symbol
// reader is the layer that lost the property, it is reachable without loading a BC closure
// (no "application" floor — see .claude/rules/no-base-app-in-csharp-tests.md), and everything
// downstream of ParsedField.RelationArms is already covered by #2528's own tests and by
// tests/runner-extras/precompiled-table-relation.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Joins CacheRootsSerialCollection for the same reason BcAppSymbolCacheTableExtTests does:
// GetTableExtensions resolves its on-disk path through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCacheTableExtRelationTests
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

    // Four fields, one extension, mirroring the four shapes Base Application actually ships on
    // this path:
    //   5900 "Service Zone Code"   — plain single-arm relation, the shape 6450 "Serv. Customer"
    //                                declares on Customer. MUST arrive with the arm AND with
    //                                RelationValidate true.
    //   5901 "Unvalidated Code"    — same relation, ValidateTableRelation = 0. The negative
    //                                control for the SECOND property: a fix that switched
    //                                validation on wholesale instead of reading both properties
    //                                makes this read true and the test fails.
    //   5902 "No Relation"         — declares none. Must arrive with null arms, so "read the
    //                                property" is not confused with "invent one".
    //   5903 "Ship-to Filter"      — FlowFilter carrying a TableRelation, exactly the shape
    //                                6450 declares. BC keeps a FlowFilter's relation (#2789,
    //                                corpus codeunit 60483), so it MUST arrive with its arm.
    //
    // Plus one table, 70789, read by the TABLE loop: a FlowFilter and a FlowField each carrying
    // a TableRelation (#2789). The two loops read the property the same way for every class.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            {
              "Id": 70789,
              "Name": "Relation Field Class",
              "Fields": [
                {
                  "TypeDefinition": { "Name": "Integer" },
                  "Properties": [],
                  "Id": 1,
                  "Name": "Entry No."
                },
                {
                  "TypeDefinition": { "Name": "Code[20]" },
                  "Properties": [
                    { "Name": "FieldClass", "Value": "FlowFilter" },
                    { "Name": "TableRelation", "Value": "Location" }
                  ],
                  "Id": 2,
                  "Name": "Location Filter"
                },
                {
                  "TypeDefinition": { "Name": "Guid" },
                  "Properties": [
                    { "Name": "FieldClass", "Value": "FlowField" },
                    { "Name": "CalcFormula", "Value": "lookup(\"G/L Account\".SystemId where(\"No.\" = field(\"Location Filter\")))" },
                    { "Name": "TableRelation", "Value": "\"G/L Account\".SystemId" }
                  ],
                  "Id": 3,
                  "Name": "Account Id"
                }
              ],
              "Keys": [ { "Name": "PK", "FieldNames": [ "Entry No." ] } ]
            }
          ],
          "Namespaces": [
            {
              "Name": "Microsoft.Service.Customer",
              "TableExtensions": [
                {
                  "TargetObject": "#437dbf0e84ff417a965ded2bb9650972#Customer",
                  "Id": 6450,
                  "Name": "Serv. Customer",
                  "Fields": [
                    {
                      "TypeDefinition": { "Name": "Code[10]" },
                      "Properties": [
                        { "Name": "Caption", "Value": "Service Zone Code" },
                        { "Name": "TableRelation", "Value": "\"Service Zone\"" }
                      ],
                      "Id": 5900,
                      "Name": "Service Zone Code"
                    },
                    {
                      "TypeDefinition": { "Name": "Code[10]" },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "\"Service Zone\"" },
                        { "Name": "ValidateTableRelation", "Value": "0" }
                      ],
                      "Id": 5901,
                      "Name": "Unvalidated Code"
                    },
                    {
                      "TypeDefinition": { "Name": "Code[10]" },
                      "Properties": [
                        { "Name": "Caption", "Value": "No Relation" }
                      ],
                      "Id": 5902,
                      "Name": "No Relation"
                    },
                    {
                      "TypeDefinition": { "Name": "Code[10]" },
                      "Properties": [
                        { "Name": "FieldClass", "Value": "FlowFilter" },
                        { "Name": "TableRelation", "Value": "\"Ship-to Address\".Code" }
                      ],
                      "Id": 5903,
                      "Name": "Ship-to Filter"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static TableExtensionSymbol ParseOnce(string dir)
    {
        var appPath = WriteApp(dir, SymbolReference);
        return Assert.Single(BcAppSymbolCache.GetTableExtensions(appPath));
    }

    [Fact]
    public void PlainRelation_ReachesRelationArmsWithTheRelatedTableName()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tableext-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var field = ParseOnce(dir).Fields.Single(f => f.FieldId == 5900);

            Assert.NotNull(field.RelationArms);
            var arm = Assert.Single(field.RelationArms!);
            // The related TABLE by name is the whole point: FieldRef.Relation is computed from
            // it, and before #3177 this was null so it answered 0.
            Assert.Equal("Service Zone", arm.TableName);
            Assert.True(field.RelationValidate);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ValidateTableRelationZero_IsReadAsItsOwnProperty_RelationStillPresent()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tableext-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var field = ParseOnce(dir).Fields.Single(f => f.FieldId == 5901);

            // Both halves, and both matter. The relation is still READABLE (FieldRef.Relation
            // must answer "Service Zone")...
            Assert.NotNull(field.RelationArms);
            Assert.Equal("Service Zone", Assert.Single(field.RelationArms!).TableName);
            // ...while the CHECK is off. A fix that read only TableRelation reports true here.
            Assert.False(field.RelationValidate);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FieldWithoutTableRelation_KeepsNullArmsAndDefaultsToValidating()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tableext-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var field = ParseOnce(dir).Fields.Single(f => f.FieldId == 5902);

            Assert.Null(field.RelationArms);
            // AL's default when ValidateTableRelation is undeclared is true, and the table loop
            // reports true for such a field — the two paths have to agree here as well.
            Assert.True(field.RelationValidate);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FlowFilterTableRelation_IsCarried_OnTheExtensionLoop()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tableext-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var field = ParseOnce(dir).Fields.Single(f => f.FieldId == 5903);

            Assert.True(field.IsFlowFilter);
            // #2789: BC keeps it — Relation() answers "Ship-to Address", and rename propagation
            // skips the field by FieldClass on BC's side, not by a missing relation.
            Assert.NotNull(field.RelationArms);
            Assert.Equal("Ship-to Address", Assert.Single(field.RelationArms!).TableName);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FlowFilterAndFlowFieldTableRelation_AreCarried_OnTheTableLoop()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tableext-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var table = Assert.Single(BcAppSymbolCache.Get(appPath).Tables, t => t.TableId == 70789);

            var filter = table.Fields.Single(f => f.FieldId == 2);
            Assert.True(filter.IsFlowFilter);
            Assert.NotNull(filter.RelationArms);
            Assert.Equal("Location", Assert.Single(filter.RelationArms!).TableName);

            var flow = table.Fields.Single(f => f.FieldId == 3);
            Assert.True(flow.IsFlowField);
            Assert.NotNull(flow.RelationArms);
            Assert.Equal("G/L Account", Assert.Single(flow.RelationArms!).TableName);

            // Control on the same table: a Normal field with no TableRelation stays null.
            Assert.Null(table.Fields.Single(f => f.FieldId == 1).RelationArms);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
