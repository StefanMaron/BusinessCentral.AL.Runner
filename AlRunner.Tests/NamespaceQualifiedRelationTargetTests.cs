// NamespaceQualifiedRelationTargetTests — runner-mechanism guard for #2851.
//
// The gap
// -------
// AL lets a TableRelation target, and a CalcFormula source, name their table through its
// NAMESPACE: `Microsoft.Manufacturing.Capacity."Capacity Ledger Entry"`. The runner's parser
// counted the dot-separated parts of that name and accepted only one (`Table`) or two
// (`Table.Field` / `Namespace.Table`), refusing anything longer — so the WHOLE relation was
// dropped and FieldRef.Relation answered 0, which AL cannot distinguish from "this field
// declares no TableRelation" (#2851, the same silent zero #2518 was reported as).
//
// CalcFormula had the same blind spot from the other direction: it never split the name at
// all, taking `Left.ToString()` verbatim, so a namespace-qualified source table arrived as the
// literal string "Microsoft.Manufacturing.Forecast.\"Production Forecast Entry\"", matched no
// table, and BuildMetaCalcFormula returned null — a FlowField that silently never computes.
//
// Measured on Base Application 28.1.49838.53910 by reading SymbolReference.json directly:
// 8 of the 7,787 relation-bearing fields name a namespace-qualified target (one of the 8,
// Item."Production Forecast Name", is a FlowFilter and is skipped a level earlier by the
// #2781 field-class gate, which is why #2851 counts 7 reaching the parser), and 4 of the
// 2,055 CalcFormula properties name a namespace-qualified source table:
//
//   Gen. Journal Line."Alloc. Acc. Modified by User"  exist(Microsoft.Finance.AllocationAccount."Alloc. Acc. Manual Override" where(...))
//   Item."Prod. Forecast Quantity (Base)"             sum(Microsoft.Manufacturing.Forecast."Production Forecast Entry"."Forecast Quantity (Base)" where(...))
//   Purchase Line."Alloc. Acc. Modified by User"      exist(Microsoft.Finance.AllocationAccount."Alloc. Acc. Manual Override" where(...))
//   User Group Plan."Plan Name"                       lookup(System.Azure.Identity.Plan.Name where(...))
//
// What this file pins
// -------------------
// The runner-only C# mechanism: what BcAppSymbolCache reads back out of a PRECOMPILED
// dependency's SymbolReference.json for a field whose TableRelation target, or whose
// CalcFormula source, is namespace-qualified. The BC-observable claim underneath it — that a
// namespace-qualified TableRelation still makes FieldRef.Relation answer the related table's
// id — is plain BC behaviour and belongs upstream in the al-language corpus against a real
// service tier, not here.
//
// Why the assertions are about the LAST TWO parts rather than a resolved table id
// -------------------------------------------------------------------------------
// Splitting `Namespace.Parts.Table` from `Namespace.Parts.Table."Field"` needs symbol
// resolution, which the parser does not have; the id resolution happens later, in
// BuildMetaFieldRelations, which already disambiguates a two-part name by trying `Table.Field`
// first and falling back to reading the last part as the table. So the parser's job is to stop
// refusing and to hand that resolver the two parts it can act on, and that is what is asserted
// here. Verified against the real closure (Base Application + System Application + Business
// Foundation + System.app, 28.1) that the fallback lands on the right table for all six
// distinct shapes Base Application actually ships: `Capacity`, `Forecast`, `Identity` and
// `Reflection` are namespace segments that are NOT table names, while `Plan`,
// `AllObjWithCaption` and `Production Forecast Name` are real tables that really do carry the
// field named after them.
//
// The .app shape below (a plain zip holding SymbolReference.json) mirrors
// TableRelationWhereFieldLinkTests and BcAppSymbolCacheReportTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection for the same reason
// BcAppSymbolCacheReportTests does.
[Collection(CacheRootsSerialCollection.Name)]
public class NamespaceQualifiedRelationTargetTests
{
    private const int TargetTableId = 88512701;
    private const int ChildTableId = 88512702;

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

    // The namespace segments are named so that neither can be mistaken for the table: no
    // table is called "NqrRoot" or "NqrLeaf", and the target table's own name shares no word
    // with them. That matters because the assertion below is about WHICH two parts survive —
    // a fix that kept the FIRST two parts, or that kept the whole dotted string as one name,
    // would produce a different pair and fail rather than pass.
    //
    // Field 2 is `Namespace.Namespace.Table` (last part IS the table); field 3 is
    // `Namespace.Namespace.Table.Field` (last part is the FIELD). Those two shapes are the
    // two Base Application actually ships and they disagree about what the last part means,
    // so both are pinned. Field 4 is the two-part form that parsed before #2851 and must keep
    // parsing. Field 5 declares no TableRelation at all. Fields 6 and 7 put a namespace-
    // qualified target inside a where() clause and inside an if/else chain, so a fix that
    // only handled the bare unconditional shape does not pass. Field 12 is a namespace-
    // qualified CalcFormula source with no field part (count), field 13 the same with one
    // (sum), and field 14 the un-namespaced control for both.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "NQR",
              "Tables": [
                {
                  "Id": 88512701,
                  "Name": "NQR Target",
                  "Fields": [
                    { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    { "Id": 2, "Name": "Target Group", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    { "Id": 3, "Name": "Name", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "50" } } },
                    { "Id": 4, "Name": "Amount", "TypeDefinition": { "Name": "Decimal" } }
                  ],
                  "Keys": [ { "Name": "PK", "FieldNames": [ "Code" ] } ]
                },
                {
                  "Id": 88512702,
                  "Name": "NQR Child",
                  "Fields": [
                    { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } },
                    {
                      "Id": 2, "Name": "Ns Table Only",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "NqrRoot.NqrLeaf.\"NQR Target\"" }
                      ]
                    },
                    {
                      "Id": 3, "Name": "Ns Table And Field",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "50" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "NqrRoot.NqrLeaf.\"NQR Target\".Name" }
                      ]
                    },
                    {
                      "Id": 4, "Name": "Plain Ref",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "\"NQR Target\".\"Code\"" }
                      ]
                    },
                    { "Id": 5, "Name": "No Relation", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    {
                      "Id": 6, "Name": "Ns With Where",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "NqrRoot.NqrLeaf.\"NQR Target\".\"Code\" where(\"Target Group\" = const('GRP'))" }
                      ]
                    },
                    {
                      "Id": 7, "Name": "Ns Conditional",
                      "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } },
                      "Properties": [
                        { "Name": "TableRelation", "Value": "if (Kind = const(A)) NqrRoot.NqrLeaf.\"NQR Target\".\"Code\"\r\n            else\r\n            \"NQR Target\".\"Code\"" }
                      ]
                    },
                    {
                      "Id": 8, "Name": "Kind",
                      "TypeDefinition": { "Name": "Option" },
                      "Properties": [ { "Name": "OptionMembers", "Value": "A,B" } ]
                    },
                    { "Id": 9, "Name": "Child Group", "TypeDefinition": { "Name": "Code", "Subtype": { "Name": "20" } } },
                    {
                      "Id": 12, "Name": "Ns Count",
                      "TypeDefinition": { "Name": "Integer" },
                      "Properties": [
                        { "Name": "FieldClass", "Value": "FlowField" },
                        { "Name": "CalcFormula", "Value": "count(NqrRoot.NqrLeaf.\"NQR Target\")" }
                      ]
                    },
                    {
                      "Id": 13, "Name": "Ns Sum",
                      "TypeDefinition": { "Name": "Decimal" },
                      "Properties": [
                        { "Name": "FieldClass", "Value": "FlowField" },
                        { "Name": "CalcFormula", "Value": "sum(NqrRoot.NqrLeaf.\"NQR Target\".Amount where(\"Target Group\" = field(\"Child Group\")))" }
                      ]
                    },
                    {
                      "Id": 14, "Name": "Plain Count",
                      "TypeDefinition": { "Name": "Integer" },
                      "Properties": [
                        { "Name": "FieldClass", "Value": "FlowField" },
                        { "Name": "CalcFormula", "Value": "count(\"NQR Target\")" }
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

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void NamespaceQualifiedTarget_IsCarried_KeepingTheLastTwoNameParts()
    {
        var dir = NewDir();
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // `NqrRoot.NqrLeaf."NQR Target"` — the last part IS the table, so the pair handed
            // to BuildMetaFieldRelations reads as `NqrLeaf."NQR Target"`, which its existing
            // Table.Field-first / Namespace.Table-fallback resolver lands on the table. Before
            // #2851 RelationArms was null here: the whole relation refused for having three
            // parts, which every consumer reads as "declares no TableRelation".
            var tableOnly = FieldOf(appPath, ChildTableId, 2);
            Assert.NotNull(tableOnly.RelationArms);
            var tableOnlyArm = Assert.Single(tableOnly.RelationArms!);
            Assert.Equal("NqrLeaf", tableOnlyArm.TableName);
            Assert.Equal("NQR Target", tableOnlyArm.FieldName);
            Assert.Empty(tableOnlyArm.Conditions);
            Assert.Empty(tableOnlyArm.Filters);

            // `NqrRoot.NqrLeaf."NQR Target".Name` — four parts, and here the last part is the
            // FIELD. The two shapes disagree about what the last part means, so keeping the
            // last two is the only reading that serves both; asserting them apart is what
            // stops a fix that always treats the last part as the table.
            var tableAndField = FieldOf(appPath, ChildTableId, 3);
            var tableAndFieldArm = Assert.Single(tableAndField.RelationArms!);
            Assert.Equal("NQR Target", tableAndFieldArm.TableName);
            Assert.Equal("Name", tableAndFieldArm.FieldName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NamespaceQualifiedTarget_KeepsItsWhereClauseAndItsArms()
    {
        var dir = NewDir();
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // A where() clause on a namespace-qualified target must survive with it. Dropping
            // the filter while keeping the target would WIDEN the relation — Validate would
            // start accepting rows outside the filter, a wrong answer rather than a missing
            // one.
            var withWhere = Assert.Single(FieldOf(appPath, ChildTableId, 6).RelationArms!);
            Assert.Equal("NQR Target", withWhere.TableName);
            Assert.Equal("Code", withWhere.FieldName);
            var f = Assert.Single(withWhere.Filters);
            Assert.Equal(ParsedCalcFilterKind.Const, f.Kind);
            Assert.Equal("Target Group", f.SourceFieldName);
            Assert.Equal("GRP", f.Value);

            // An if/else chain with the qualified target in the FIRST arm only. Both arms must
            // arrive, and in order: a fix that refused the chain the moment one arm was
            // qualified would lose the un-qualified arm too, which is exactly how the
            // whole-property refusal behaved.
            var arms = FieldOf(appPath, ChildTableId, 7).RelationArms;
            Assert.NotNull(arms);
            Assert.Equal(2, arms!.Count);
            Assert.Equal("NQR Target", arms[0].TableName);
            Assert.Equal("Code", arms[0].FieldName);
            Assert.Equal(ParsedCalcFilterKind.Const, Assert.Single(arms[0].Conditions).Kind);
            Assert.Equal("Kind", Assert.Single(arms[0].Conditions).SourceFieldName);
            // The else arm carries no condition of its own — BC's own shape, pinned upstream.
            Assert.Equal("NQR Target", arms[1].TableName);
            Assert.Equal("Code", arms[1].FieldName);
            Assert.Empty(arms[1].Conditions);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UnqualifiedRelations_AreUnchanged_AndAFieldWithoutOneStillHasNoArms()
    {
        var dir = NewDir();
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // Positive control: the two-part shape that already worked must still produce the
            // same pair, so "the parser stopped counting parts at all" is not what makes the
            // tests above pass.
            var plain = Assert.Single(FieldOf(appPath, ChildTableId, 4).RelationArms!);
            Assert.Equal("NQR Target", plain.TableName);
            Assert.Equal("Code", plain.FieldName);

            // Negative direction: a field with no TableRelation must still carry no arms.
            // Without this, "RelationArms is never null any more" would pass as a fix and
            // FieldRef.Relation would start answering non-zero for fields that declare
            // nothing.
            Assert.Null(FieldOf(appPath, ChildTableId, 5).RelationArms);

            // Control: the .app is genuinely readable and both tables arrived, so every
            // "is null" assertion is an observation rather than a vacuous "nothing loaded".
            var tables = BcAppSymbolCache.Get(appPath).Tables;
            Assert.Contains(tables, t => t.TableId == TargetTableId);
            Assert.Contains(tables, t => t.TableId == ChildTableId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NamespaceQualifiedCalcFormulaSource_ResolvesToTheTableName()
    {
        var dir = NewDir();
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            // count/exist carry a table and no field, so the last part is unambiguously the
            // table — no fallback needed, unlike the TableRelation case. Before #2851 this was
            // the literal string `NqrRoot.NqrLeaf."NQR Target"`, which matched no table, and
            // BuildMetaCalcFormula returned null: the FlowField silently never computed.
            var nsCount = FieldOf(appPath, ChildTableId, 12);
            Assert.NotNull(nsCount.CalcFormula);
            Assert.Equal("Count", nsCount.CalcFormula!.FormulaType, ignoreCase: true);
            Assert.Equal("NQR Target", nsCount.CalcFormula.SourceTableName);
            Assert.Null(nsCount.CalcFormula.SourceFieldName);

            // sum/lookup carry Table.Field, so the namespace sits inside the LEFT half only —
            // the field name must come through untouched, and the where() clause with it.
            var nsSum = FieldOf(appPath, ChildTableId, 13);
            Assert.NotNull(nsSum.CalcFormula);
            Assert.Equal("Sum", nsSum.CalcFormula!.FormulaType, ignoreCase: true);
            Assert.Equal("NQR Target", nsSum.CalcFormula.SourceTableName);
            Assert.Equal("Amount", nsSum.CalcFormula.SourceFieldName);
            var w = Assert.Single(nsSum.CalcFormula.Filters);
            Assert.Equal(ParsedCalcFilterKind.Field, w.Kind);
            Assert.Equal("Target Group", w.SourceFieldName);
            Assert.Equal("Child Group", w.ParentFieldName);

            // Positive control: the un-namespaced form must be unchanged, so a fix that
            // mangled every CalcFormula source name equally would not pass.
            var plainCount = FieldOf(appPath, ChildTableId, 14);
            Assert.NotNull(plainCount.CalcFormula);
            Assert.Equal("NQR Target", plainCount.CalcFormula!.SourceTableName);

            // Negative direction: a field that is not a FlowField has no CalcFormula at all.
            Assert.Null(FieldOf(appPath, ChildTableId, 4).CalcFormula);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
