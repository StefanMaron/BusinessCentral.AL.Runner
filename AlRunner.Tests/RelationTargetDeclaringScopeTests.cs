// RelationTargetDeclaringScopeTests — runner-side guard for #4106.
//
// The BC half (a Base Application TableRelation and CalcFormula keep pointing at Base
// Application tables when a dependent app declares a same-named table in its own namespace)
// is proved upstream by BusinessCentral.AL.Language.Tests "Test Relation Name Collision"
// (60988). These tests pin the runner's own selection rule in
// RecordPatches.ResolveTableNameInDeclaringScope, staged without a .app on disk: which table a
// NAME resolves to depends on whether the table that wrote the name came from a dependency's
// symbols, and a same-.app candidate wins over another dependency's.
using System.Collections;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class RelationTargetDeclaringScopeTests
{
    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    private const string AppA = "/deps/Microsoft_Base Application.app";
    private const string AppB = "/deps/Contoso_Other.app";

    // Ids outside every other parser test's range so a leak is obvious.
    private const int DeclaringId = 61940;   // dependency table writing the name (app A)
    private const int BundleTargetId = 61941; // bundle table sharing the name
    private const int DepTargetId = 61942;   // dependency table with the name (app A)
    private const int OtherDepTargetId = 61943; // same name, another dependency (app B)
    private const int BundleDeclaringId = 61944;
    private const int PlatformOnlyId = 61945;

    private const string TargetName = "RTS Shipping Agent";

    private static ParsedTable Table(int id, string name, Guid? owningApp = null) =>
        new(id, name, new List<ParsedField>(), new List<int>(), OwningAppId: owningApp);

    [Fact]
    public void DependencyDeclaredName_ResolvesToTheDependencyTable_NotTheSameNamedBundleTable()
    {
        var bundleApp = Guid.NewGuid();
        WithState(
            parsed: new[] { Table(BundleTargetId, TargetName, bundleApp) },
            index: new[]
            {
                (AppA, Table(DeclaringId, "RTS Customer")),
                (AppA, Table(DepTargetId, TargetName)),
            },
            act: () =>
            {
                var declaring = SymbolTable(DeclaringId);
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, declaring);

                Assert.NotNull(resolved);
                Assert.Equal(DepTargetId, resolved!.TableId);
                // Materialised into _parsedTables, so metadata built from it later finds it.
                Assert.True(ParsedTables().Contains(DepTargetId));
            });
    }

    [Fact]
    public void DependencyDeclaredName_PrefersTheDeclaringApp_OverAnotherDependency()
    {
        WithState(
            parsed: Array.Empty<ParsedTable>(),
            index: new[]
            {
                (AppB, Table(OtherDepTargetId, TargetName)),
                (AppA, Table(DepTargetId, TargetName)),
                (AppA, Table(DeclaringId, "RTS Customer")),
            },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, SymbolTable(DeclaringId));
                Assert.Equal(DepTargetId, resolved?.TableId);
            });
    }

    [Fact]
    public void BundleDeclaredName_KeepsTheByNameLookup()
    {
        var bundleApp = Guid.NewGuid();
        var bundleDeclaring = Table(BundleDeclaringId, "RTS Bundle Referencing", bundleApp);
        WithState(
            parsed: new[] { Table(BundleTargetId, TargetName, bundleApp), bundleDeclaring },
            index: new[] { (AppA, Table(DepTargetId, TargetName)) },
            act: () =>
            {
                // A bundle table's names resolve in the bundle's own scope, which this change
                // does not model; the pre-#4106 lookup (parsed tables first) stands.
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, bundleDeclaring);
                Assert.Equal(BundleTargetId, resolved?.TableId);
            });
    }

    [Fact]
    public void DependencyDeclaredName_NotInAnySymbolFile_FallsBackToParsedTables()
    {
        WithState(
            parsed: new[] { Table(PlatformOnlyId, "RTS Platform Table") },
            index: new[] { (AppA, Table(DeclaringId, "RTS Customer")) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope("RTS Platform Table", SymbolTable(DeclaringId));
                Assert.Equal(PlatformOnlyId, resolved?.TableId);
                Assert.Null(RecordPatches.ResolveTableNameInDeclaringScope("RTS Nowhere", SymbolTable(DeclaringId)));
            });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IDictionary ParsedTables() => (IDictionary)RecordPatchesType
        .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static FieldInfo IndexField() =>
        RecordPatchesType.GetField("_bcSymbolTableIndex", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static ParsedTable SymbolTable(int id) =>
        ((Dictionary<int, (string AppPath, ParsedTable Table)>)IndexField().GetValue(null)!)[id].Table;

    private static void WithState(ParsedTable[] parsed, (string App, ParsedTable Table)[] index, Action act)
    {
        var ids = parsed.Select(t => t.TableId).Concat(index.Select(e => e.Table.TableId)).ToArray();
        var parsedTables = ParsedTables();
        var previousIndex = IndexField().GetValue(null);
        foreach (var id in ids)
            Assert.False(parsedTables.Contains(id), $"table id {id} is already in _parsedTables; pick another range");
        try
        {
            foreach (var t in parsed) parsedTables[t.TableId] = t;
            var staged = new Dictionary<int, (string AppPath, ParsedTable Table)>();
            foreach (var (app, t) in index) staged[t.TableId] = (app, t);
            IndexField().SetValue(null, staged);
            act();
        }
        finally
        {
            foreach (var id in ids) parsedTables.Remove(id);
            IndexField().SetValue(null, previousIndex);
        }
    }
}
