// TableRelationWhereFieldLinkTests — runner-mechanism guard for #2518.
//
// The gap
// -------
// RecordPatches.AlSourceParser.RelationConditionList carried const(...) and filter(...) and
// nothing else, for BOTH halves of a TableRelation: the if(...) conditions AND the where(...)
// filters. Anything else hit the default arm, which refuses the WHOLE property — so a relation
// written as
//
//     TableRelation = "Post Code".City where("Country/Region Code" = field("Country/Region Code"))
//
// reached the runner's metadata with NO relations at all. FieldRef.Relation then answered 0,
// which is indistinguishable from "this field declares no TableRelation", and RapidStart's
// Config. Package Field."Relation Table ID" stayed 0 (issue #2518). Measured on Base
// Application 28.1.49838.53910: 1,155 of the 7,787 relation-bearing fields declare a
// where(... = field(...)), Customer.City among them.
//
// The refusal is not symmetric, and that is BC's own shape rather than a runner preference:
//   - a where(...) entry becomes a MetaFilter, and NCLMetaFilter.CreateFromMetaFilter has a
//     FilterType.FIELD case building an NCLMetaFilterField whose value is read off the
//     referencing row at evaluation time;
//   - an if(...) condition becomes a MetaCondition, and NCLMetaFilter.CreateFromMetaCondition
//     has CONST and FILTER cases only and throws NotSupportedException on FIELD.
// Both read from the decompiled Microsoft.Dynamics.Nav.Ncl.dll 28.1.
//
// What this file pins
// -------------------
// The runner-only C# mechanism: what BcAppSymbolCache reads back out of a PRECOMPILED
// dependency's SymbolReference.json for a field whose TableRelation carries a field() link —
// the arm's target, the filter's kind, and which side of the relation each of the two field
// names belongs to. The BC-observable claim underneath it (FieldRef.Relation answers the
// related table's id for such a field, and 0 for a field with no TableRelation) is plain BC
// behaviour and is asserted upstream in the al-language corpus against a real service tier,
// not here.
//
// The .app shape below (a plain zip holding SymbolReference.json) mirrors
// BcAppSymbolCacheReportTests and DependencyReportProcessingOnlyTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection for the same reason
// BcAppSymbolCacheReportTests does.
[Collection(CacheRootsSerialCollection.Name)]
public class TableRelationWhereFieldLinkTests
{
    private const int ParentTableId = 88123701;
    private const int ChildTableId = 88123702;

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

    // Field 3 is the shape #2518 is about. Field 4 is the same relation WITHOUT the field()
    // link — it parsed before the fix and must keep parsing, so "the parser stopped refusing
    // everything" cannot pass as a fix. Field 5 declares no TableRelation at all (the "must
    // still be null" direction). Fields 6/7 are AL's two mode-carrying spellings of the same
    // FIELD link, which BC models as MetaFilter.ValueIsFilter / .OnlyMaxLimit rather than as
    // separate kinds; field 8 is the Date the upperlimit one links to. Field 9 is
    // Customer.City's two-arm shape. Field 10 puts a field() link in the if(...) CONDITION,
    // which BC's MetaCondition cannot represent — it must still refuse the whole property.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "DRWF",
              "Tables": [
                {
                  "Id": 88123701,
                  "Name": "DRWF Parent",
                  "Fields": [
                    { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    { "Id": 2, "Name": "Group Code", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    { "Id": 3, "Name": "Date Filter", "TypeDefinition": { "Name": "Date" } }
                  ],
                  "Keys": [ { "Name": "PK", "FieldNames": [ "Code" ] } ]
                },
                {
                  "Id": 88123702,
                  "Name": "DRWF Child",
                  "Fields": [
                    { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } },
                    { "Id": 2, "Name": "Group Code", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    {
                      "Id": 3, "Name": "Where Field Ref",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "\"DRWF Parent\".\"Code\" where(\"Group Code\" = field(\"Group Code\"))" }
                      ]
                    },
                    {
                      "Id": 4, "Name": "Plain Ref",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "\"DRWF Parent\".\"Code\"" }
                      ]
                    },
                    { "Id": 5, "Name": "No Relation", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    {
                      "Id": 6, "Name": "Filter Link Ref",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "\"DRWF Parent\".\"Code\" where(\"Group Code\" = field(filter(\"Group Code\")))" }
                      ]
                    },
                    {
                      "Id": 7, "Name": "Upper Limit Ref",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "\"DRWF Parent\".\"Code\" where(\"Date Filter\" = field(upperlimit(\"Date Filter\")))" }
                      ]
                    },
                    { "Id": 8, "Name": "Date Filter", "TypeDefinition": { "Name": "Date" } },
                    {
                      "Id": 9, "Name": "Conditional Where Ref",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "if (\"Group Code\" = const('')) \"DRWF Parent\".\"Code\"\r\n            else\r\n            if (\"Group Code\" = filter(<> '')) \"DRWF Parent\".\"Code\" where(\"Group Code\" = field(\"Group Code\"))" }
                      ]
                    },
                    {
                      "Id": 10, "Name": "Field Link In Condition",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "if (\"Group Code\" = field(\"Group Code\")) \"DRWF Parent\".\"Code\"" }
                      ]
                    }
                  ],
                  "Keys": [ { "Name": "PK", "FieldNames": [ "Entry No." ] } ]
                }
              ]
            }
          ]
        }
        """;

    private static ParsedField FieldOf(string appPath, int tableId, int fieldId)
    {
        var table = Assert.Single(BcAppSymbolCache.Get(appPath).Tables, t => t.TableId == tableId);
        return Assert.Single(table.Fields, f => f.FieldId == fieldId);
    }

    [Fact]
    public void WhereClauseFieldLink_IsCarried_AsAFieldFilterNamingTheReferencingField()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var whereFieldRef = FieldOf(appPath, ChildTableId, 3);

            // Before #2518 this was null — the whole property refused — which every consumer
            // reads as "this field declares no TableRelation".
            Assert.NotNull(whereFieldRef.RelationArms);
            var arm = Assert.Single(whereFieldRef.RelationArms!);

            // The arm still names its target concretely; a fix that carried the filter but
            // lost the target would leave FieldRef.Relation answering 0 all the same.
            Assert.Equal("DRWF Parent", arm.TableName);
            Assert.Equal("Code", arm.FieldName);
            Assert.Empty(arm.Conditions);

            var filter = Assert.Single(arm.Filters);
            Assert.Equal(ParsedCalcFilterKind.Field, filter.Kind);

            // The two names are NOT interchangeable, and the pair is the whole point: the
            // left-hand name is a field of the RELATED table (DRWF Parent) and the field()
            // argument is a field of the REFERENCING table (DRWF Child). BC's
            // NCLMetaFilterField reads filterValue as a field of the table it is handed —
            // which NCLMetaField's ctor sets to the referencing table — so swapping them
            // builds a filter against the wrong row. Both are spelled "Group Code" here on
            // purpose, exactly as Customer.City's is, so the assertions below pin the
            // ROLES rather than the spelling.
            Assert.Equal("Group Code", filter.SourceFieldName);
            Assert.Equal("Group Code", filter.ParentFieldName);

            // A plain field() link carries neither mode flag; fields 6 and 7 below are the
            // spellings that do.
            Assert.False(filter.ValueIsFilter);
            Assert.False(filter.OnlyMaxLimit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RelationsWithoutAFieldLink_AreUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // Positive control: the shape that already worked must keep working, so a
            // "parser that now accepts everything" is not what makes the test above pass.
            var plain = FieldOf(appPath, ChildTableId, 4);
            var plainArm = Assert.Single(plain.RelationArms!);
            Assert.Equal("DRWF Parent", plainArm.TableName);
            Assert.Empty(plainArm.Filters);

            // Negative direction: a field with no TableRelation must still carry no arms.
            // Without this, "RelationArms is never null any more" would pass as a fix and
            // FieldRef.Relation would start answering non-zero for fields that declare
            // nothing.
            Assert.Null(FieldOf(appPath, ChildTableId, 5).RelationArms);

            // The asymmetry, asserted rather than described: a field() link in the if(...)
            // CONDITION stays refused. BC's NCLMetaFilter.CreateFromMetaCondition has CONST
            // and FILTER cases only and throws NotSupportedException on FIELD, so carrying
            // one would build metadata BC cannot load. Widening both halves together would
            // pass every other test in this file and fail here.
            Assert.Null(FieldOf(appPath, ChildTableId, 10).RelationArms);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FieldLinkModeSpellings_SetTheMatchingMetaFilterFlag()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // field(filter(X)) — MetaFilter.ValueIsFilter. BC reads the referencing field's
            // value as a filter EXPRESSION over the related field rather than as a value to
            // compare against, so getting this flag wrong changes which rows match.
            var filterLink = Assert.Single(Assert.Single(FieldOf(appPath, ChildTableId, 6).RelationArms!).Filters);
            Assert.Equal(ParsedCalcFilterKind.Field, filterLink.Kind);
            Assert.Equal("Group Code", filterLink.ParentFieldName);
            Assert.True(filterLink.ValueIsFilter);
            Assert.False(filterLink.OnlyMaxLimit);

            // field(upperlimit(X)) — MetaFilter.OnlyMaxLimit, and NOT ValueIsFilter. The two
            // flags are independent; asserting both directions on both fields is what stops
            // a fix that sets them together from passing.
            var upperLimit = Assert.Single(Assert.Single(FieldOf(appPath, ChildTableId, 7).RelationArms!).Filters);
            Assert.Equal(ParsedCalcFilterKind.Field, upperLimit.Kind);
            Assert.Equal("Date Filter", upperLimit.ParentFieldName);
            Assert.False(upperLimit.ValueIsFilter);
            Assert.True(upperLimit.OnlyMaxLimit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConditionalRelation_KeepsBothArms_WhenOnlyTheSecondCarriesAFieldLink()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // Customer.City's exact shape, spelled the way SymbolReference.json spells it
            // (embedded \r\n between the arms). One unrepresentable where() entry refused
            // the WHOLE chain, so the first arm — which has no where() at all — was lost
            // with it. That is why Customer.City answered 0 on a blank record, where the
            // arm that matches is the first one.
            var arms = FieldOf(appPath, ChildTableId, 9).RelationArms;
            Assert.NotNull(arms);
            Assert.Equal(2, arms!.Count);

            Assert.Equal("DRWF Parent", arms[0].TableName);
            Assert.Empty(arms[0].Filters);
            Assert.Equal(ParsedCalcFilterKind.Const, Assert.Single(arms[0].Conditions).Kind);

            Assert.Equal("DRWF Parent", arms[1].TableName);
            Assert.Equal(ParsedCalcFilterKind.Filter, Assert.Single(arms[1].Conditions).Kind);
            Assert.Equal(ParsedCalcFilterKind.Field, Assert.Single(arms[1].Filters).Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParentTableRelationsAreUnaffected_AndTheAppReallyLoaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // Control: the .app is genuinely readable and both tables arrived, so every
            // "RelationArms is null" assertion above is an observation rather than a
            // vacuous "nothing was loaded".
            var tables = BcAppSymbolCache.Get(appPath).Tables;
            Assert.Contains(tables, t => t.TableId == ParentTableId);
            Assert.Contains(tables, t => t.TableId == ChildTableId);
            Assert.All(Assert.Single(tables, t => t.TableId == ParentTableId).Fields,
                f => Assert.Null(f.RelationArms));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
