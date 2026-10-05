// RelationTargetDeclaringScopeTests — runner-side guard for #4106.
//
// The BC half (a Base Application TableRelation and CalcFormula keep pointing at Base
// Application tables when a dependent app declares a same-named table in its own namespace)
// is proved upstream by BusinessCentral.AL.Language.Tests "Test Relation Name Collision"
// (60988). These tests pin the runner's own selection rule in
// RecordPatches.ResolveTableNameInDeclaringScope, staged without a .app on disk: which table a
// NAME resolves to depends on whether the table that wrote the name came from a dependency's
// symbols, and a same-.app candidate wins over another dependency's.
//
// #4133 adds the bundle-declared half: a name written by a table parsed from AL source resolves
// in that file's scope, its own namespace first, then the global namespace and its `using`
// directives. The BC half of that is proved upstream by corpus codeunit 69210 "Test Relation
// Target NS Scope" (StefanMaron/BusinessCentral.AL.Language.Tests#538).
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

    private const int ImportedBundleId = 61946;
    private const int OtherBundleId = 61947;

    private static ParsedTable SourceTable(int id, string name, string? ns, params string[] usings) =>
        new(id, name, new List<ParsedField>(), new List<int>(), OwningAppId: Guid.NewGuid(),
            Namespace: ns, Usings: usings);

    [Fact]
    public void SourceDeclaredName_NotInScope_ResolvesToTheDependencyTable_NotTheOutOfScopeBundleTable()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Other", "Microsoft.Foundation.Shipping");
        WithState(
            parsed: new[] { SourceTable(BundleTargetId, TargetName, "Test.Dup"), writer },
            index: new[] { (AppA, Table(DepTargetId, TargetName)) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(DepTargetId, resolved?.TableId);
            },
            freshParsedTables: true);
    }

    [Fact]
    public void SourceDeclaredName_InTheSameNamespace_ResolvesToTheBundleTable()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Dup", "Microsoft.Foundation.Shipping");
        WithState(
            parsed: new[] { SourceTable(BundleTargetId, TargetName, "Test.Dup"), writer },
            index: new[] { (AppA, Table(DepTargetId, TargetName)) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(BundleTargetId, resolved?.TableId);
            });
    }

    [Fact]
    public void SourceDeclaredName_InAnImportedNamespace_ResolvesToTheBundleTable()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Other", "Test.Dup");
        WithState(
            parsed: new[] { SourceTable(BundleTargetId, TargetName, "Test.Dup"), writer },
            index: new[] { (AppA, Table(DepTargetId, TargetName)) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(BundleTargetId, resolved?.TableId);
            });
    }

    [Fact]
    public void SourceDeclaredName_InTheGlobalNamespace_IsInScopeEverywhere()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Other");
        WithState(
            parsed: new[] { SourceTable(BundleTargetId, TargetName, null), writer },
            index: new[] { (AppA, Table(DepTargetId, TargetName)) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(BundleTargetId, resolved?.TableId);
            });
    }

    // Own namespace before imported is the compiler's order.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceDeclaredName_InScopeInTheOwnNamespaceAndImported_TheOwnNamespaceWins(bool ownFirst)
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Own", "Test.Imported");
        var own = SourceTable(OtherBundleId, TargetName, "Test.Own");
        var imported = SourceTable(ImportedBundleId, TargetName, "Test.Imported");
        WithState(
            parsed: ownFirst ? new[] { own, imported, writer } : new[] { imported, own, writer },
            index: Array.Empty<(string, ParsedTable)>(),
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(OtherBundleId, resolved?.TableId);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceDeclaredName_TwoBundleTablesInDifferentNamespaces_TheImportedOneWins(bool importedFirst)
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Other", "Test.Imported");
        var imported = SourceTable(ImportedBundleId, TargetName, "Test.Imported");
        var other = SourceTable(OtherBundleId, TargetName, "Test.Unrelated");
        WithState(
            parsed: importedFirst ? new[] { imported, other, writer } : new[] { other, imported, writer },
            index: Array.Empty<(string, ParsedTable)>(),
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(ImportedBundleId, resolved?.TableId);
            });
    }

    // #5224. A dependency table is read with the namespace its symbol file states, and a name
    // the writer's own namespace holds as a dependency's table means that table, not an imported
    // bundle table. The BC half is corpus codeunit 69217 "Test Relation Own NS Dep".
    private static ParsedTable DependencyTable(int id, string name, string? ns) =>
        new(id, name, new List<ParsedField>(), new List<int>(), Namespace: ns);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceDeclaredName_OwnNamespaceDependencyAndImportedBundle_TheDependencyWins(bool importedFirst)
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Own", "Test.Imported");
        var imported = SourceTable(ImportedBundleId, TargetName, "Test.Imported");
        WithState(
            parsed: importedFirst ? new[] { imported, writer } : new[] { writer, imported },
            index: new[] { (AppA, DependencyTable(DepTargetId, TargetName, "test.own")) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(DepTargetId, resolved?.TableId);
            });
    }

    // The global namespace is the own namespace of a file that states none.
    [Fact]
    public void SourceDeclaredName_GlobalNamespaceWriter_GlobalDependencyBeatsAnImportedBundle()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", null, "Test.Imported");
        var imported = SourceTable(ImportedBundleId, TargetName, "Test.Imported");
        WithState(
            parsed: new[] { imported, writer },
            index: new[] { (AppA, DependencyTable(DepTargetId, TargetName, null)) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(DepTargetId, resolved?.TableId);
            });
    }

    // The other half of the order: a dependency table in some OTHER namespace is not in scope
    // ahead of an imported bundle table, so the bundle table keeps the name.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceDeclaredName_DependencyInAnotherNamespace_ImportedBundleStillWins(bool importedFirst)
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Own", "Test.Imported");
        var imported = SourceTable(ImportedBundleId, TargetName, "Test.Imported");
        WithState(
            parsed: importedFirst ? new[] { imported, writer } : new[] { writer, imported },
            index: new[] { (AppA, DependencyTable(DepTargetId, TargetName, "Test.Elsewhere")) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(ImportedBundleId, resolved?.TableId);
            });
    }

    // Nothing in scope in the own namespace or through a using: the name still means a
    // dependency's table, whatever namespace that one sits in (the step before #5224).
    [Fact]
    public void SourceDeclaredName_OnlyADependencyInAnotherNamespace_StillResolvesToIt()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Own", "Test.Imported");
        WithState(
            parsed: new[] { SourceTable(BundleTargetId, TargetName, "Test.Unrelated"), writer },
            index: new[] { (AppA, DependencyTable(DepTargetId, TargetName, "Test.Elsewhere")) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(DepTargetId, resolved?.TableId);
            },
            freshParsedTables: true);
    }

    // Both in the own namespace: the runner takes the bundle table, as before #5224. A runner
    // choice, not a BC claim: no corpus test declares a bundle table over a dependency's name in
    // the dependency's own namespace.
    [Fact]
    public void SourceDeclaredName_OwnNamespaceBundleAndOwnNamespaceDependency_TheBundleTableKeepsIt()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Own");
        var own = SourceTable(OtherBundleId, TargetName, "Test.Own");
        WithState(
            parsed: new[] { own, writer },
            index: new[] { (AppA, DependencyTable(DepTargetId, TargetName, "Test.Own")) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(OtherBundleId, resolved?.TableId);
            });
    }

    // A dependency table already loaded into _parsedTables has no namespace (Namespace == null), so
    // without the source-parsed filter it would read as global and be in scope from every file.
    // Staged ahead of the bundle table on a fresh dictionary so enumeration order cannot hide it.
    [Fact]
    public void SourceDeclaredName_MaterialisedDependencyTable_IsNotTreatedAsGlobal()
    {
        var writer = SourceTable(BundleDeclaringId, "RTS Writer", "Test.Other", "Test.Dup");
        var dep = Table(DepTargetId, TargetName);
        WithState(
            parsed: new[] { dep, SourceTable(BundleTargetId, TargetName, "Test.Dup"), writer },
            index: new[] { (AppA, dep) },
            act: () =>
            {
                var resolved = RecordPatches.ResolveTableNameInDeclaringScope(TargetName, writer);
                Assert.Equal(BundleTargetId, resolved?.TableId);
            },
            freshParsedTables: true);
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

    // The parser half: the scope a resolver reads must be the one the FILE states.
    [Fact]
    public void ParsedFromSource_CarriesTheFilesNamespaceAndUsings_OnTheTableAndOnAnExtensionField()
    {
        const int tableId = 61948;
        const string tableName = "RTS Scope Parse Fixture";
        var parse = (string name, string text) => RecordPatchesType
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] { text, null });
        try
        {
            parse("TryParseTableFile", $$"""
                namespace Test.Own;

                using Test.Imported;
                using System.IO;

                table {{tableId}} "{{tableName}}"
                {
                    fields { field(1; Code; Code[10]) { } }
                    keys { key(PK; Code) { Clustered = true; } }
                }
                """);
            parse("TryParseTableExtensionFile", $$"""
                namespace Test.ExtOwn;

                using Test.ExtImported;

                tableextension 61949 "RTS Scope Parse Ext" extends "{{tableName}}"
                {
                    fields { field(61949; "Ext Field"; Code[10]) { } }
                }
                """);

            var table = (ParsedTable)ParsedTables()[tableId]!;
            Assert.Equal("Test.Own", table.Namespace);
            Assert.Equal(new[] { "Test.Imported", "System.IO" }, table.Usings);

            var extFields = ((Dictionary<string, List<ParsedField>>)RecordPatchesType
                .GetField("_parsedExtensionFields", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
                [tableName.ToLowerInvariant()];
            var ext = Assert.Single(extFields);
            Assert.Equal("Test.ExtOwn", ext.ScopeNamespace);
            Assert.Equal(new[] { "Test.ExtImported" }, ext.ScopeUsings);
            // The base table's own fields state no scope of their own: the table's applies.
            Assert.All(table.Fields, f => Assert.Null(f.ScopeUsings));
        }
        finally
        {
            ParsedTables().Remove(tableId);
            ((Dictionary<string, List<ParsedField>>)RecordPatchesType
                .GetField("_parsedExtensionFields", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
                .Remove(tableName.ToLowerInvariant());
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IDictionary ParsedTables() => (IDictionary)RecordPatchesType
        .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static FieldInfo IndexField() =>
        RecordPatchesType.GetField("_bcSymbolTableIndex", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static ParsedTable SymbolTable(int id) =>
        ((Dictionary<int, (string AppPath, ParsedTable Table)>)IndexField().GetValue(null)!)[id].Table;

    // freshParsedTables: Clear() resets the Dictionary's free list, so the staged tables enumerate in
    // the order they were added whatever other classes of this collection left behind. A test whose
    // answer would otherwise depend on which table _parsedTables enumerates first says so here.
    private static void WithState(ParsedTable[] parsed, (string App, ParsedTable Table)[] index, Action act,
        bool freshParsedTables = false)
    {
        if (freshParsedTables)
        {
            var map = ParsedTables();
            var saved = map.Keys.Cast<object>().Select(k => (k, map[k])).ToList();
            map.Clear();
            try { WithState(parsed, index, act); }
            finally
            {
                map.Clear();
                foreach (var (k, v) in saved) map[k] = v;
            }
            return;
        }

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
