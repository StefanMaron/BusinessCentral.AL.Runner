// ObjectRefConstCallSiteWiringTests — issue #3207, the coverage half.
//
// WHAT WAS UNPINNED, AND HOW IT WAS MEASURED
// ------------------------------------------
// #3205 added RecordPatches.ResolveObjectReferenceConst and applied it at the THREE places a
// const value becomes BC metadata: a CalcFormula where() filter, a TableRelation where()
// filter, and a TableRelation if() arm condition. Its runner-local test
// (ObjectReferenceConstResolutionTests) pins the resolver IN ISOLATION — it calls the resolver
// directly and never builds any metadata — so deleting all three call sites left the whole C#
// suite green. The wiring was covered only by corpus codeunit 60329, which this repository does
// not run yet: the submodule pin is deliberately held behind master (#3152).
//
// So each call site is driven here through the real builder, with a real
// Microsoft.Dynamics.Nav.Types.Metadata.MetaFilter / MetaCondition coming back out and its
// value read. Revert any one of the three to the pre-#3205 `filter.Value ?? ""` and exactly one
// of these three facts goes red, naming which.
//
// WHY THIS IS NOT A BC-BEHAVIOUR TEST
// -----------------------------------
// The claim "a CalcFormula whose where() names const(Database::Customer) filters on the
// Customer table's id" is a statement about Business Central, and it is asked upstream, on a
// real service tier, in StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60329. Nothing
// here restates it. What is asserted here is a RUNNER mechanism: that the runner's own
// metadata builder routes a const value through its own resolver before handing it to BC, on
// the PRECOMPILED-dependency route (the object being named is declared only in a registered
// SymbolReference package, never in AL source) that the corpus structurally cannot reach —
// every object a corpus test can name is source-compiled.
//
// WHY THE BUILDER IS CALLED BY REFLECTION
// ---------------------------------------
// BuildMetaCalcFormula and BuildMetaFieldRelations are private, and their public entry point is
// the full NCLMetaTable build, which needs the whole engine standing up. The reflection
// statics they read (_tMetaCalcFormula, _tMetaFilter, _tMetaCondition, _tMetaFieldRelation,
// _tFilterType) are otherwise assigned only by RecordPatches.Register(); they are set here from
// the SAME Microsoft.Dynamics.Nav.Types types Register() resolves, which this test project
// already references directly, so the assignment is idempotent with Register() rather than a
// substitute for it.
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: registers a symbol .app into the process-global _bcAppPaths, writes the
// metadata reflection statics, and calls ResetForReload().
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ObjectRefConstCallSiteWiringTests : IDisposable
{
    private const int SourceTableId = 70971;
    private const int ReferencedTableId = 70981;
    private const int ReferencedReportId = 70991;

    private readonly string _root;
    private readonly Dictionary<string, object?> _savedStatics = new();

    public ObjectRefConstCallSiteWiringTests()
    {
        _root = TestScratch.Dir("al-runner-objref-const-wiring");
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
        EnsureMetadataReflection();
        RecordPatches.AddBcAppPath(WriteSymbolApp());
    }

    public void Dispose()
    {
        // Put the reflection statics back exactly as found — including "null", the state they
        // are in before RecordPatches.Register() runs. This class writes process-global fields
        // it does not own, and a test asserting the pre-Register() path must not silently find
        // them populated by whichever class happened to run first.
        foreach (var (name, value) in _savedStatics)
            try { Static(name).SetValue(null, value); } catch { }
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // no-base-app-in-csharp-tests.md: a bare SymbolReference package, no application floor. The
    // referenced table/report exist ONLY here — not in any .al source — which is the precompiled
    // route the corpus cannot express.
    private string WriteSymbolApp()
    {
        var symbolReference = System.Text.Json.JsonSerializer.Serialize(new
        {
            AppId = Guid.NewGuid().ToString(),
            Name = "ORW Wiring Fixture",
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = new object[]
            {
                new
                {
                    Id = SourceTableId,
                    Name = "ORW Source Row",
                    Fields = new object[]
                    {
                        new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } },
                        new { Id = 2, Name = "Table ID",  TypeDefinition = new { Name = "Integer" } },
                        new { Id = 3, Name = "Report ID", TypeDefinition = new { Name = "Integer" } },
                    },
                },
                new
                {
                    Id = ReferencedTableId,
                    Name = "ORW Referenced Row",
                    Fields = new object[]
                    {
                        new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } },
                    },
                },
            },
            Reports = new object[] { new { Id = ReferencedReportId, Name = "ORW Referenced Report" } },
            Codeunits = Array.Empty<object>(),
            Pages = Array.Empty<object>(),
            Queries = Array.Empty<object>(),
            XmlPorts = Array.Empty<object>(),
            EnumTypes = Array.Empty<object>(),
        });

        var appPath = Path.Combine(_root, "orw-wiring-fixture.app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
            w.Write(symbolReference);
        return appPath;
    }

    // The table declaring the FlowField / the relation. Never registered anywhere: the builders
    // take it as an argument, which is what lets this test drive them without a bundle.
    private static ParsedTable ReferencingTable() => new(
        70961, "ORW Referencing Row",
        new List<ParsedField>
        {
            new(1, "Entry No.", "Integer", 0),
            new(2, "Coupled Count", "Integer", 0),
        },
        new List<int> { 1 });

    // ── CALL SITE 3: the CalcFormula where() filter ─────────────────────────────────────────

    [Fact]
    public void ACalcFormulaWhereConst_ReachesBcMetadataAsTheObjectId()
    {
        var formula = new ParsedCalcFormula("Count", "ORW Source Row", null, new List<ParsedCalcFilter>
        {
            new("Table ID", ParsedCalcFilterKind.Const, null, "Database::\"ORW Referenced Row\""),
        });

        var meta = Invoke("BuildMetaCalcFormula", formula, ReferencingTable());
        Assert.NotNull(meta);

        var filter = Assert.Single(Enumerate(Get(meta!, "Filters")));
        Assert.Equal("70981", (string)Get(filter, "FilterValue"));
        // The const-ness is part of the claim: an id handed over as a FIELD filter would mean
        // "field 70981 of the source table", which is a different and equally wrong metadata.
        Assert.Equal("CONST", Get(filter, "FilterType").ToString());
        Assert.Equal(2, (int)Get(filter, "FieldId"));
    }

    [Fact]
    public void ACalcFormulaWhereConst_ThatIsNotAnObjectReference_IsStillPassedThroughUntouched()
    {
        // The control. A resolver applied too eagerly here would rewrite the 1215 const
        // conditions the Base Application ships that are not object references.
        var formula = new ParsedCalcFormula("Count", "ORW Source Row", null, new List<ParsedCalcFilter>
        {
            new("Table ID", ParsedCalcFilterKind.Const, null, "42"),
        });

        var meta = Invoke("BuildMetaCalcFormula", formula, ReferencingTable());
        var filter = Assert.Single(Enumerate(Get(meta!, "Filters")));
        Assert.Equal("42", (string)Get(filter, "FilterValue"));
    }

    // ── CALL SITES 1 AND 2: the TableRelation if() condition and where() filter ─────────────

    [Fact]
    public void ATableRelationIfConditionConst_ReachesBcMetadataAsTheObjectId()
    {
        // `TableRelation = if ("Coupled Count" = const(Report::"ORW Referenced Report"))
        //                     "ORW Referenced Row"`. The condition is keyed on a field of the
        // REFERENCING table, which is what makes this a distinct call site from the where().
        var arm = new ParsedRelationArm("ORW Referenced Row", null,
            new List<ParsedCalcFilter>
            {
                new("Coupled Count", ParsedCalcFilterKind.Const, null, "Report::\"ORW Referenced Report\""),
            },
            new List<ParsedCalcFilter>());

        var relations = Invoke("BuildMetaFieldRelations",
            new List<ParsedRelationArm> { arm }, ReferencingTable(), "Coupled Count");
        Assert.NotNull(relations);

        var relation = Assert.Single(Enumerate(relations!));
        var condition = Assert.Single(Enumerate(Get(relation, "Conditions")));
        Assert.Equal("70991", (string)Get(condition, "ConditionValue"));
        Assert.Equal("CONST", Get(condition, "ConditionType").ToString());
        Assert.Equal(2, (int)Get(condition, "FieldId"));
    }

    [Fact]
    public void ATableRelationWhereConst_ReachesBcMetadataAsTheObjectId()
    {
        // `TableRelation = "ORW Source Row"."Entry No." where("Table ID" =
        //                     const(Database::"ORW Referenced Row"))`. The where() is keyed on a
        // field of the RELATED table — the other of the two call sites in this builder.
        var arm = new ParsedRelationArm("ORW Source Row", "Entry No.",
            new List<ParsedCalcFilter>(),
            new List<ParsedCalcFilter>
            {
                new("Table ID", ParsedCalcFilterKind.Const, null, "Database::\"ORW Referenced Row\""),
            });

        var relations = Invoke("BuildMetaFieldRelations",
            new List<ParsedRelationArm> { arm }, ReferencingTable(), "Coupled Count");
        Assert.NotNull(relations);

        var relation = Assert.Single(Enumerate(relations!));
        var filter = Assert.Single(Enumerate(Get(relation, "Filters")));
        Assert.Equal("70981", (string)Get(filter, "FilterValue"));
        Assert.Equal("CONST", Get(filter, "FilterType").ToString());
        Assert.Equal(2, (int)Get(filter, "FieldId"));
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────

    /// <summary>Assign the metadata reflection statics RecordPatches.Register() would, from the
    /// same Microsoft.Dynamics.Nav.Types types. Idempotent with Register(): identical values.</summary>
    private void EnsureMetadataReflection()
    {
        var types = typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaTable).Assembly;
        foreach (var (field, typeName) in new[]
                 {
                     ("_tMetaCalcFormula",  "Microsoft.Dynamics.Nav.Types.Metadata.MetaCalcFormula"),
                     ("_tMetaFilter",       "Microsoft.Dynamics.Nav.Types.Metadata.MetaFilter"),
                     ("_tMetaCondition",    "Microsoft.Dynamics.Nav.Types.Metadata.MetaCondition"),
                     ("_tMetaFieldRelation","Microsoft.Dynamics.Nav.Types.Metadata.MetaFieldRelation"),
                     ("_tFilterType",       "Microsoft.Dynamics.Nav.Types.Metadata.FilterType"),
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
        var p = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (p == null)
            throw new InvalidOperationException(
                $"{target.GetType().FullName} has no property '{property}'. It has: " +
                string.Join(", ", target.GetType().GetProperties().Select(x => x.Name)));
        return p.GetValue(target)
               ?? throw new InvalidOperationException($"{target.GetType().Name}.{property} was null.");
    }

    private static List<object> Enumerate(object immutableArrayOrList)
        => ((IEnumerable)immutableArrayOrList).Cast<object>().ToList();
}
