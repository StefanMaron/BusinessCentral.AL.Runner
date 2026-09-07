// CalcFormulaExtensionFieldWiringTests — the runner-mechanism half of #3263 and #3279.
//
// WHAT THIS PINS
// --------------
// RecordPatches.BuildMetaCalcFormula resolves every field name a CalcFormula states — the
// source field, a where-arm's own field on the source table, and the parent field a
// `field(...)` arm names — against the fields a TABLEEXTENSION merged onto the table as well as
// the table's own (#3263), and, when a name resolves nowhere, builds NO formula and records why
// (#3279) instead of dropping that one arm and answering with the rest.
//
// The second half is the one worth stating plainly. Dropping an arm is not a diagnostic-level
// problem: the FlowField still calculates, over rows the dropped condition was supposed to
// exclude, and returns a WRONG NUMBER with nothing in the output at default verbosity. That is
// the silent default `.claude/rules/loud-failures.md` exists to prevent, so the refusal is
// asserted here, with the text that names the table and the field.
//
// WHY THIS IS NOT A BC-BEHAVIOUR TEST
// -----------------------------------
// "A CalcFormula may name a field a tableextension added, on either side of the link" is a
// statement about Business Central, and it is asked upstream on a real service tier — corpus
// codeunit 60823 ("TXC Tests", StefanMaron/BusinessCentral.AL.Language.Tests#217), verified on
// a BC 28.4.53241.0 container. Nothing here restates it. What is asserted here is the runner's
// own metadata mechanism, including two things a corpus test structurally cannot reach: which
// field id the runner hands BC, and what the runner does with a name it CANNOT resolve — AL
// that names a field that does not exist never compiles, so only a runner gap produces it.
//
// WHY THE BUILDER IS CALLED BY REFLECTION
// ---------------------------------------
// Same reason as ObjectRefConstCallSiteWiringTests, whose harness this follows:
// BuildMetaCalcFormula is private and its public entry point needs the whole engine standing
// up. The metadata reflection statics are set from the SAME Microsoft.Dynamics.Nav.Types types
// RecordPatches.Register() resolves, so the assignment is idempotent with Register().
using System.Collections;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: writes the metadata reflection statics, the parsed-table and
// parsed-extension-field indexes, and calls ResetForReload().
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CalcFormulaExtensionFieldWiringTests : IDisposable
{
    private const int ParentTableId = 70841;
    private const int SourceTableId = 70842;

    // Field ids chosen so a wrong resolution cannot coincide with a right one: the extension
    // fields are numbered far away from the tables' own fields.
    private const int ParentOwnNoFieldId = 1;
    private const int ParentExtFlowFilterId = 70851;
    private const int SourceOwnAmountFieldId = 4;
    private const int SourceOwnLinkFieldId = 2;
    private const int SourceExtWeightFieldId = 70852;

    private readonly Dictionary<string, object?> _savedStatics = new();

    public CalcFormulaExtensionFieldWiringTests()
    {
        RecordPatches.ResetForReload();
        EnsureMetadataReflection();
        RegisterTables();
    }

    public void Dispose()
    {
        foreach (var (field, value) in _savedStatics)
            Static(field).SetValue(null, value);
        RecordPatches.ResetForReload();
    }

    // ── #3263: a tableextension field resolves on both sides of the link ───────────────────

    [Fact]
    public void ASumOverATableExtensionFieldOnTheSourceTable_ReachesBcMetadataAsThatFieldsId()
    {
        // CalcFormula = sum("CFX Line"."CFX Ext Weight" where("Doc No." = field("No.")))
        var formula = new ParsedCalcFormula("Sum", "CFX Line", "CFX Ext Weight",
            new List<ParsedCalcFilter>
            {
                new("Doc No.", ParsedCalcFilterKind.Field, "No.", null),
            });

        var meta = BuildFormula(formula, flowFieldId: 3);

        Assert.NotNull(meta);
        // The whole claim: BC is handed the EXTENSION field's id, not 0 and not the table's
        // own Amount field. Before #3263 this call returned null instead.
        Assert.Equal(SourceExtWeightFieldId, (int)Get(meta!, "FieldId"));
        Assert.Equal(SourceTableId, (int)Get(meta!, "TableId"));
        Assert.False(RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 3, out _),
            "a formula that built must leave no unresolved-reference note behind");
    }

    [Fact]
    public void AWhereArmOnATableExtensionFlowFilter_ReachesBcMetadataAsThatFieldsId()
    {
        // CalcFormula = sum("CFX Line".Amount where("Doc No." = field("No."),
        //                                           "CFX Ext Weight" = field("CFX Ext Date Filter")))
        var formula = new ParsedCalcFormula("Sum", "CFX Line", "Amount",
            new List<ParsedCalcFilter>
            {
                new("Doc No.", ParsedCalcFilterKind.Field, "No.", null),
                new("CFX Ext Weight", ParsedCalcFilterKind.Field, "CFX Ext Date Filter", null),
            });

        var meta = BuildFormula(formula, flowFieldId: 4);

        Assert.NotNull(meta);
        Assert.Equal(SourceOwnAmountFieldId, (int)Get(meta!, "FieldId"));

        var filters = Enumerate(Get(meta!, "Filters")).ToList();
        // Two arms, both kept. Before #3263 the second one was dropped and the aggregate ran
        // with one condition, which is the wrong-number half of this defect.
        Assert.Equal(2, filters.Count);

        var extensionArm = filters[1];
        // The arm's own field is the extension field on the SOURCE table...
        Assert.Equal(SourceExtWeightFieldId, (int)Get(extensionArm, "FieldId"));
        // ...and its value is the id of the extension FlowFilter on the PARENT table, which is
        // how BC reads a FIELD filter.
        Assert.Equal("FIELD", Get(extensionArm, "FilterType").ToString());
        Assert.Equal(ParentExtFlowFilterId.ToString(), (string)Get(extensionArm, "FilterValue"));
    }

    // ── #3279: an unresolvable reference refuses instead of dropping the arm ───────────────

    [Fact]
    public void AnUnresolvableWhereArmField_BuildsNoFormulaAndRecordsWhichFieldAndTable()
    {
        var formula = new ParsedCalcFormula("Sum", "CFX Line", "Amount",
            new List<ParsedCalcFilter>
            {
                new("Doc No.", ParsedCalcFilterKind.Field, "No.", null),
                new("CFX Field That Does Not Exist", ParsedCalcFilterKind.Const, null, "7"),
            });

        var meta = BuildFormula(formula, flowFieldId: 5);

        // Not "a formula with one arm" — no formula at all. The old behaviour returned a
        // MetaCalcFormula carrying only the resolvable arm, so this assertion is what
        // distinguishes the fix from the defect.
        Assert.Null(meta);

        Assert.True(
            RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 5, out var reason),
            "an unresolvable reference must be recorded against the FlowField, so CalcFields can "
            + "name it instead of raising BC's \"You must define a CalcFormula\"");
        Assert.Contains("CFX Field That Does Not Exist", reason);
        Assert.Contains("CFX Line", reason);
    }

    [Fact]
    public void AnUnresolvableSourceField_BuildsNoFormulaAndRecordsWhichFieldAndTable()
    {
        var formula = new ParsedCalcFormula("Sum", "CFX Line", "CFX Absent Amount",
            new List<ParsedCalcFilter>
            {
                new("Doc No.", ParsedCalcFilterKind.Field, "No.", null),
            });

        var meta = BuildFormula(formula, flowFieldId: 6);

        Assert.Null(meta);
        Assert.True(
            RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 6, out var reason));
        Assert.Contains("CFX Absent Amount", reason);
        Assert.Contains("CFX Line", reason);
    }

    [Fact]
    public void ARebuildThatSucceeds_ClearsTheEarlierUnresolvedNote()
    {
        // #3121's late-registration retry rebuilds a table whose source table arrived in a .app
        // registered afterwards. A note left over from the first attempt would then refuse a
        // FlowField that has a formula again.
        var unresolvable = new ParsedCalcFormula("Sum", "CFX Line", "CFX Absent Amount",
            new List<ParsedCalcFilter>());
        Assert.Null(BuildFormula(unresolvable, flowFieldId: 7));
        Assert.True(RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 7, out _));

        var resolvable = new ParsedCalcFormula("Sum", "CFX Line", "CFX Ext Weight",
            new List<ParsedCalcFilter>());
        Assert.NotNull(BuildFormula(resolvable, flowFieldId: 7));

        Assert.False(RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 7, out _),
            "a successful rebuild of the same FlowField must drop the earlier note");
    }

    [Fact]
    public void ResetForReload_DropsEveryUnresolvedNote()
    {
        var unresolvable = new ParsedCalcFormula("Sum", "CFX Line", "CFX Absent Amount",
            new List<ParsedCalcFilter>());
        Assert.Null(BuildFormula(unresolvable, flowFieldId: 8));
        Assert.True(RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 8, out _));

        RecordPatches.ResetForReload();

        Assert.False(RecordPatches.TryGetUnresolvedCalcFormulaReference(ParentTableId, 8, out _),
            "a note from the previous bundle must not refuse the next bundle's FlowField");
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────

    private static object? BuildFormula(ParsedCalcFormula formula, int flowFieldId)
        => Invoke("BuildMetaCalcFormula", formula, ParentTable(), flowFieldId);

    /// <summary>The FlowField's own table: two declared fields, and one FlowFilter that only a
    /// tableextension contributes (registered through MergeExtensionFields below).</summary>
    private static ParsedTable ParentTable() => new(
        ParentTableId, "CFX Header",
        new List<ParsedField>
        {
            new(ParentOwnNoFieldId, "No.", "Code", 20),
            new(2, "Descr", "Text", 50),
        },
        new List<int> { ParentOwnNoFieldId });

    private static ParsedTable SourceTable() => new(
        SourceTableId, "CFX Line",
        new List<ParsedField>
        {
            new(1, "Entry No.", "Integer", 0),
            new(SourceOwnLinkFieldId, "Doc No.", "Code", 20),
            new(SourceOwnAmountFieldId, "Amount", "Decimal", 0),
        },
        new List<int> { 1 });

    /// <summary>Put both tables in the parsed-table index and give each the extension field
    /// this test's formulas name, exactly as a parsed tableextension would.</summary>
    private void RegisterTables()
    {
        var parsedTables = (System.Collections.IDictionary)Static("_parsedTables").GetValue(null)!;
        parsedTables[ParentTableId] = ParentTable();
        parsedTables[SourceTableId] = SourceTable();

        MergeExtensionFields("CFX Header", 70843, new[]
        {
            new ParsedField(ParentExtFlowFilterId, "CFX Ext Date Filter", "Date", 0,
                IsFlowFilter: true),
        });
        MergeExtensionFields("CFX Line", 70844, new[]
        {
            new ParsedField(SourceExtWeightFieldId, "CFX Ext Weight", "Decimal", 0),
        });
    }

    private static void MergeExtensionFields(string baseTableName, int extensionId,
        IEnumerable<ParsedField> fields)
    {
        var m = typeof(RecordPatches).GetMethod("MergeExtensionFields",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.MergeExtensionFields not found — this test drives it.");
        m.Invoke(null, new object?[] { baseTableName, extensionId, fields, null });
    }

    /// <summary>Assign the metadata reflection statics RecordPatches.Register() would, from the
    /// same Microsoft.Dynamics.Nav.Types types. Idempotent with Register(): identical values.</summary>
    private void EnsureMetadataReflection()
    {
        var types = typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaTable).Assembly;
        foreach (var (field, typeName) in new[]
                 {
                     ("_tMetaCalcFormula", "Microsoft.Dynamics.Nav.Types.Metadata.MetaCalcFormula"),
                     ("_tMetaFilter",      "Microsoft.Dynamics.Nav.Types.Metadata.MetaFilter"),
                     ("_tFilterType",      "Microsoft.Dynamics.Nav.Types.Metadata.FilterType"),
                 })
        {
            var t = types.GetType(typeName)
                    ?? throw new InvalidOperationException($"{typeName} not found in {types.GetName().Name}.");
            var f = Static(field);
            _savedStatics[field] = f.GetValue(null);
            f.SetValue(null, t);
        }
    }

    private static FieldInfo Static(string name) =>
        typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"RecordPatches.{name} not found — this test tracks that field.");

    private static object? Invoke(string method, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"RecordPatches.{method} not found — this test drives it.");
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new InvalidOperationException($"{method} threw: {tie.InnerException.Message}", tie.InnerException);
        }
    }

    /// <summary>A property of a BC metadata object, by name. Throws listing what IS there rather
    /// than returning null, so a renamed member on a future BC build reads as a named failure.</summary>
    private static object Get(object target, string property)
    {
        var p = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"{target.GetType().Name} has no property '{property}'. It has: "
                    + string.Join(", ", target.GetType().GetProperties().Select(x => x.Name)));
        return p.GetValue(target)
               ?? throw new InvalidOperationException($"{target.GetType().Name}.{property} is null.");
    }

    private static IEnumerable<object> Enumerate(object collection)
        => ((IEnumerable)collection).Cast<object>();
}
