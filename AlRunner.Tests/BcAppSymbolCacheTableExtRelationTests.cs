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
// Two of the four FAIL before the fix and pass after it
// (PlainRelation_..., ValidateTableRelationZero_...); the other two PASS in both states and are
// GUARDS on the shape of the fix, not RED -> GREEN evidence. Said plainly so nobody reads
// "4 passed" as four proofs: FieldWithoutTableRelation_... refuses "invent a relation where the
// symbol declares none", and FlowFilterTableRelation_... refuses "carry it for every field
// class". Each is verified by breaking the fix the corresponding way, not by the run below.
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
    //                                6450 declares. The table loop gates relation parsing on
    //                                !IsFlowField && !IsFlowFilter with a documented reason
    //                                (#2528); this pins the extension loop to the same gate, so
    //                                the two paths cannot disagree in the other direction.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
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
    public void FlowFilterTableRelation_IsNotCarried_MatchingTheTablePathsGate()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-tableext-relation");
        Directory.CreateDirectory(dir);
        try
        {
            var field = ParseOnce(dir).Fields.Single(f => f.FieldId == 5903);

            Assert.True(field.IsFlowFilter);
            // Refused on purpose (#2528's gate), not missed: a FlowFilter's TableRelation is a
            // lookup hint for the filter's own UI, and carrying it would pull FlowFilter
            // pseudo-columns into the rename-propagation reverse index.
            Assert.Null(field.RelationArms);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
