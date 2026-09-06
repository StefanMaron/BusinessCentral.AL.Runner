// CalcFormulaLateSourceTableRebuildTests — proves #3121 differential 1: a table built while
// the .app declaring its FlowField's CalcFormula SOURCE table was not registered yet must be
// rebuilt once that .app registers, so the field ends up carrying a real formula instead of
// NCLMetaCalculationFormula.EmptyFormula.
//
// Why a direct unit test, not an AL fixture
// -----------------------------------------
// The same reason #2126 gave for the sibling file next to this one
// (RecordPatchesPrecompiledTableExtEvictionTests): the ordering is the whole subject, and an
// AL bundle cannot force it. A `tests/runner-extras/` suite shipping the dependency as a
// checked-in .app in `.alpackages/` was written and measured for this issue — it passes
// BEFORE the fix, because through `.alpackages` the dependency's table is parsed lazily,
// after the Base Application symbol index is already built, so the ordering never occurs.
// It was dropped rather than committed green-in-both-directions. Driving RecordPatches'
// own entry points puts the two events in the order the defect needs:
//
//   1. Parse a table from AL source whose FlowField carries
//      `CalcFormula = lookup("<Source>".Name where("No." = field("Source Code")))`, and
//      materialise its NCLMetaTable FIRST — while nothing knows table "<Source>" at all.
//      BuildMetaCalcFormula cannot resolve the source table, so BC's own NCLMetaField ctor
//      stores the EmptyFormula singleton (`metaCalculationFormula != null ? ... :
//      NCLMetaCalculationFormula.EmptyFormula`), which is what CalcFields refuses with
//      "You must define a CalcFormula for the {0} FlowField in the {1} table".
//   2. Register a precompiled .app that DOES declare "<Source>".
//   3. Re-resolve the first table through the same cache-or-build entry point its callers
//      use, and read the FlowField back.
//
// Without the fix step 3 returns the instance from step 1, whose formula is still
// EmptyFormula — nothing dropped a built NCLMetaTable when the registered .app set GREW.
// With it, AddBcAppPath's retry evicts _metaTableCache plus the skeleton NCLMetadata entry
// for exactly that table and repopulates, so the rebuilt field carries a formula naming the
// source table and field.
//
// Measured RED on main and GREEN on the fix branch; ~0.3s, no Base Application floor.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CalcFormulaLateSourceTableRebuildTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public CalcFormulaLateSourceTableRebuildTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3121-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void WriteApp(string path, string symbolReferenceJson)
    {
        using var zip = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
    }

    [SkippableFact]
    public void ARegisteredAppDeclaringTheSourceTable_RebuildsTheFlowFieldsCalcFormula()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Ids are process-wide unique among AlRunner.Tests statics — these land in the same
        // static _parsedTables / _metaTableCache the whole test assembly shares — and sit
        // outside every other file's declared range (93900-93902 belong to the #2126 test
        // next door).
        const int parentTableId = 93940;
        const string parentTableName = "RevProto Parent";
        const int sourceTableId = 93941;
        const string sourceTableName = "RevProto Source";
        const int flowFieldId = 3;
        const int sourceNameFieldId = 2;

        // ── ARRANGE: the parent table, from AL source, naming a table nothing knows yet ──
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Parent.al"), $$"""
            table {{parentTableId}} "{{parentTableName}}"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; "Source Code"; Code[20]) { }
                    field({{flowFieldId}}; "Source Name"; Text[100])
                    {
                        FieldClass = FlowField;
                        CalcFormula = lookup("{{sourceTableName}}".Name where("No." = field("Source Code")));
                        Editable = false;
                    }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);
        RecordPatches.AddSourceDir(srcDir);

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        // ── STEP 1: build the parent's NCLMetaTable while the source table is unknown ────
        var before = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, parentTableId, false, 0);
        Assert.NotNull(before);
        Assert.True(before.TryGetFieldByNo(flowFieldId, out var fieldBefore),
            "sanity check: the FlowField must exist on the first build");
        Assert.Equal(FieldClass.FlowField, fieldBefore.FieldClass);

        // The pre-condition the whole fix is about, asserted rather than assumed: BC's own
        // ctor fell back to the EmptyFormula singleton because BuildMetaCalcFormula could not
        // resolve "RevProto Source". This is the state CalcFields refuses.
        Assert.Same(NCLMetaCalculationFormula.EmptyFormula, fieldBefore.CalculationFormula);

        // ── STEP 2: register a precompiled .app that DOES declare the source table ───────
        var sr = $$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "Tables": [
                {
                  "Id": {{sourceTableId}},
                  "Name": "{{sourceTableName}}",
                  "Fields": [
                    { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 1, "Name": "No." },
                    { "TypeDefinition": { "Name": "Text[100]" }, "Properties": [], "Id": {{sourceNameFieldId}}, "Name": "Name" }
                  ],
                  "Keys": [
                    { "Name": "PK", "FieldNames": [ "No." ] }
                  ]
                }
              ]
            }
            """;
        var appPath = Path.Combine(_root, "source-dep.app");
        WriteApp(appPath, sr);
        RecordPatches.AddBcAppPath(appPath);

        // ── STEP 3 / ASSERT (positive): the rebuilt field carries a real formula ─────────
        // Same cache-or-build entry point BuildNCLMetaTable's callers use. Without the fix
        // this hands back the step-1 instance, whose formula is still EmptyFormula.
        var after = RecordPatches.EnsureTableInMetadataCache(parentTableId);
        Assert.NotNull(after);
        Assert.True(after!.TryGetFieldByNo(flowFieldId, out var fieldAfter));

        var formula = fieldAfter.CalculationFormula;
        Assert.NotNull(formula);
        Assert.NotSame(NCLMetaCalculationFormula.EmptyFormula, formula);

        // Not merely "not the empty singleton": the formula has to name the table and field
        // the AL declared, or a rebuild that materialised SOMETHING would pass. EmptyFormula
        // itself carries TableId 0 / FieldId 0, so these three are what separate a resolved
        // formula from a fabricated one.
        Assert.Equal(sourceTableId, formula.TableId);
        Assert.Equal(sourceNameFieldId, formula.FieldId);
        Assert.Equal(NCLMetaCalculationMethod.Lookup, formula.CalculationMethod);

        // The where-condition survives too: `where("No." = field("Source Code"))` is one
        // FIELD filter naming the source table's field 1. A formula that dropped its filters
        // would calculate over every row of the source table.
        // Read through SourceFieldId, not SourceField: the latter resolves the field through
        // NCLMetaFilterCollection.ResolveSourceTable -> NCLMetadata.InitializeBaseAppGroup,
        // which needs app-group state this skeleton does not have and throws
        // ArgumentNullException. The id is the value the formula actually carries.
        Assert.NotNull(formula.Filters);
        var filter = Assert.Single(formula.Filters);
        Assert.Equal(NCLMetaFilterType.Field, filter.FilterType);
        Assert.Equal(1, filter.SourceFieldId);

        // ── ASSERT (negative): an ordinary column on the same rebuilt table gains nothing
        // — the retry must rebuild the table, not hand out formulas. Measured: a Normal field
        // carries a null CalculationFormula here, where the FlowField carried the EmptyFormula
        // singleton before the rebuild, so the two states are distinguishable.
        Assert.True(after.TryGetFieldByNo(2, out var plainField));
        Assert.Equal(FieldClass.Normal, plainField.FieldClass);
        Assert.Null(plainField.CalculationFormula);
    }
}
