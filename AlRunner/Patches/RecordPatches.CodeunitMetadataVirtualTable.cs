// RecordPatches.CodeunitMetadataVirtualTable — managed provider for the
// "CodeUnit Metadata" (2000000137) system virtual table.
//
// WHY THIS EXISTS (issue #2544)
//   CodeUnit Metadata is virtual on the service tier: one row per codeunit compiled into
//   the application, computed from that codeunit's own AL declaration. It routed to the
//   same empty in-memory store as every other table here, so:
//
//     CodeunitMetadata.Get(<any id>)  -> false, always
//     CodeunitMetadata.FindSet()      -> "There is no CodeUnit Metadata within the filter."
//
//   The first is a silent wrong answer, not an error. It is also the last missing member of
//   a family this file's siblings already cover: Table Metadata (2000000136),
//   Page Metadata (2000000138), Report Metadata (2000000139) and Report Data Items
//   (2000000203) all have managed providers here.
//
// WHAT REAL BC ANSWERS
//   Microsoft.Dynamics.Nav.Runtime.CodeUnitDataProvider (Ncl.dll) declares
//   TableId => 2000000137 and builds each row from NCLMetadata, keyed on field 1, in this
//   column order: the object number; the object name; NCLMetaCodeunit.TableId (AL's TableNo
//   property); IsSingleInstance, forced true for codeunit 1; Subtype; the owning app's id;
//   inherent permissions; inherent entitlements; TestType; RequiredTestIsolation; the
//   object's AL namespace. A codeunit whose metadata cannot be resolved is skipped, not
//   reported as an empty row — which is why the enumeration below drops an id it has no
//   name for instead of inserting a blank.
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Codeunits the runner compiles itself — parsed from their AL source
//      (RecordPatches.AlObjectDeclParser.cs: Name / TableNo / SingleInstance / Subtype).
//   2. Codeunits living in a PRECOMPILED dependency (Base Application, System Application,
//      ISV apps) — read from that .app's SymbolReference.json, which states the same four
//      properties (BcAppSymbolCache.Codeunits). This is the only route for an R2R app: it
//      ships no metadata XML.
//   Source-compiled codeunits win over symbol-derived ones for the same id — the source is
//   what this run actually compiled. Both feed through EnumerateKnownAlObjects, the same
//   shared inventory AllObj reads, so AllObj and CodeUnit Metadata cannot disagree about
//   which codeunits exist.
//
// COLUMNS NOT IMPLEMENTED
//   Everything outside ID / Name / TableNo / SingleInstance / Subtype — the owning app id,
//   the two permission-mask strings, TestType, RequiredTestIsolation, Namespace — gets BC's
//   own NavValue.GetDefaultNavValue for that column's type, which is also what a real row
//   carries for a codeunit that declares none of them. Filling them needs sources the
//   runner does not have yet (the app-id column needs per-object app attribution, which is
//   the same data issue #2326 tracks for AllObj's "Object Subtype"); inventing a value
//   would be a silent wrong answer, so they are left at BC's default and named here.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (NCLMetaTable, NCLMetaField, NavValue, ReadOnlyRecordBuffer,
//   TempTableDataProvider), reached through the same helpers the AllObj / Table Metadata /
//   Page Metadata providers resolve. No AL business-logic body is touched.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int CodeunitMetadataVirtualTableId = 2000000137;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, byte>>
        _cmvPopulatedByProvider = new();

    private static bool IsCodeunitMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == CodeunitMetadataVirtualTableId;

    /// <summary>
    /// One codeunit as CodeUnit Metadata exposes it. <see cref="TableNo"/> is already
    /// resolved to a real table id (0 only when the codeunit genuinely declares no
    /// <c>TableNo</c>, or when the declared table could not be resolved — which is
    /// reported, never silent; the same rule Table Metadata applies to LookupPageId).
    /// <see cref="Subtype"/> is the AL name as written (<c>Normal</c>, <c>Test</c>,
    /// <c>Install</c>, …), matched against the live option string by name at row-build time.
    /// </summary>
    private sealed record CodeunitMetaRow(int Id, string Name, int TableNo, bool SingleInstance, string Subtype);

    private static List<CodeunitMetaRow>? _codeunitMetaRows;
    private static (int AppEpoch, int Decls) _codeunitMetaRowsBuiltFrom = (-1, -1);
    private static readonly object _codeunitMetaRowsLock = new();

    // Resolved once per process from the parsed CodeUnit Metadata metatable's own "Subtype"
    // field option string — same technique AllObj uses for its "Object Type" column.
    private static Dictionary<string, int>? _cmvSubtypeOrdinals;

    /// <summary>
    /// Populate the in-memory store behind CodeUnit Metadata (2000000137) with one row per
    /// codeunit the runner knows about. Idempotent per (provider, codeunit id); called on
    /// every handout so codeunits registered later in the run still show up.
    /// </summary>
    private static void PopulateCodeunitMetadataVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create(bool)
        EnsureDataAccessProviderReflection(dataAccess);
        var subtypeOrdinals = EnsureCodeunitSubtypeOrdinals(metaTable);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "CodeUnit Metadata (virtual table 2000000137)",
                "codeunit-metadata-virtual-table — data access has no in-memory provider; see docs/scope.md");

        var done = _cmvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        foreach (var row in EnumerateKnownCodeunitMetadata())
        {
            if (!done.TryAdd(row.Id, 0)) continue;
            InsertVirtualRow(provider, metaTable,
                new object[] { CodeunitMetadataVirtualTableId, row.Id, 0, 0 },
                field => BuildCodeunitMetadataValue(field, row, subtypeOrdinals));
        }
    }

    /// <summary>
    /// One column of a CodeUnit Metadata row, matched by the metatable's own FIELD NAME so
    /// the mapping tracks whatever the System package in the resolved artifact declares
    /// rather than a hardcoded field-number table.
    /// </summary>
    private static object? BuildCodeunitMetadataValue(
        NCLMetaField field, CodeunitMetaRow row, Dictionary<string, int> subtypeOrdinals)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(
            null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "id":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.Id });
            case "name":
                return Text(row.Name);
            case "tableno":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.TableNo });
            case "singleinstance":
                return NavBoolean(row.SingleInstance);
            case "subtype":
                if (subtypeOrdinals.TryGetValue(NormalizeObjectTypeName(row.Subtype), out var ordinal))
                    return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, ordinal });
                // A Subtype this BC artifact's option set does not list (should not happen —
                // the compiler validated it against the same enum) — BC's own default rather
                // than a guessed ordinal.
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// Every codeunit the runner has real metadata for: source-parsed codeunits of the app
    /// under test and of any source-compiled dependency first, then codeunits declared by
    /// the SymbolReference.json of every registered precompiled dependency .app.
    /// </summary>
    private static List<CodeunitMetaRow> EnumerateKnownCodeunitMetadata()
    {
        var generation = (_bcAppRegistrationEpoch, _parsedObjectDecls.Count);
        if (_codeunitMetaRows != null && _codeunitMetaRowsBuiltFrom == generation) return _codeunitMetaRows;
        lock (_codeunitMetaRowsLock)
        {
            generation = (_bcAppRegistrationEpoch, _parsedObjectDecls.Count);
            if (_codeunitMetaRows != null && _codeunitMetaRowsBuiltFrom == generation) return _codeunitMetaRows;

            var rows = new Dictionary<int, CodeunitMetaRow>();
            var unresolvedTables = new List<string>();

            int ResolveTableNo(string? reference, int codeunitId)
            {
                if (string.IsNullOrWhiteSpace(reference)) return 0;   // declares none — truthful 0
                if (int.TryParse(reference, out var literal) && literal > 0) return literal;
                var resolved = ResolveTableIdByName(reference);
                if (resolved > 0) return resolved;
                unresolvedTables.Add($"codeunit {codeunitId} TableNo -> table '{reference}'");
                return 0;
            }

            // 1. Codeunits the runner source-compiled.
            foreach (var d in _parsedObjectDecls.Values)
            {
                if (NormalizeObjectTypeName(d.Kind) != "codeunit" || d.Id <= 0) continue;
                rows[d.Id] = new CodeunitMetaRow(
                    d.Id, d.Name,
                    ResolveTableNo(d.TableNo, d.Id),
                    // Codeunit 1 is single-instance in BC whatever it declares — see
                    // CodeUnitDataProvider, which ORs `item.Key == 1` into the flag.
                    d.SingleInstance || d.Id == 1,
                    d.Subtype ?? "Normal");
            }

            // 2. Codeunits declared by precompiled dependency .app packages.
            foreach (var symbol in EnumerateBcAppCodeunitSymbols())
            {
                if (rows.ContainsKey(symbol.Id)) continue;   // source-compiled wins
                rows[symbol.Id] = new CodeunitMetaRow(
                    symbol.Id, symbol.Name,
                    ResolveTableNo(symbol.TableNo, symbol.Id),
                    symbol.SingleInstance || symbol.Id == 1,
                    symbol.Subtype ?? "Normal");
            }

            if (unresolvedTables.Count > 0)
                Console.Error.WriteLine(
                    $"[RecordPatches] CodeUnit Metadata: {unresolvedTables.Count} declared TableNo reference(s) "
                    + "could not be resolved to a table id and are reported as 0: "
                    + string.Join("; ", unresolvedTables.Take(10))
                    + (unresolvedTables.Count > 10 ? $" (+{unresolvedTables.Count - 10} more)" : string.Empty));

            _codeunitMetaRows = rows.Values.ToList();
            _codeunitMetaRowsBuiltFrom = generation;
            return _codeunitMetaRows;
        }
    }

    /// <summary>
    /// Codeunits declared by the registered precompiled dependency .app packages, read off
    /// the SAME <c>Objects</c> list AllObj enumerates — so the two tables cannot disagree
    /// about which codeunits a dependency contains. The codeunit-only properties ride on
    /// <see cref="BcAppSymbolCache.ObjectSymbol"/>; see its doc comment.
    /// </summary>
    private static IEnumerable<BcAppSymbolCache.ObjectSymbol> EnumerateBcAppCodeunitSymbols()
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.ObjectSymbol> objects;
            try
            {
                objects = BcAppSymbolCache.Get(appPath).Objects;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] CodeUnit Metadata: SymbolReference read failed for "
                    + $"{Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var o in objects)
                if (o.Id > 0 && NormalizeObjectTypeName(o.Kind) == "codeunit")
                    yield return o;
        }
    }

    /// <summary>
    /// Read the "Subtype" option field's ordinals out of the parsed CodeUnit Metadata
    /// metatable's own NCLOptionMetadata.OptionString, matched by name — never a hardcoded
    /// table, so the mapping tracks whatever the System package in the resolved artifact
    /// declares. Mirrors Page Metadata's EnsurePageTypeOrdinals for its "PageType" column.
    /// </summary>
    private static Dictionary<string, int> EnsureCodeunitSubtypeOrdinals(NCLMetaTable metaTable)
    {
        if (_cmvSubtypeOrdinals != null) return _cmvSubtypeOrdinals;

        var field = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => NormalizeObjectTypeName(f.FieldName ?? string.Empty) == "subtype")
            ?? throw new RunnerOutOfScopeException(
                "CodeUnit Metadata (virtual table 2000000137)",
                "codeunit-metadata-virtual-table — metatable has no \"Subtype\" field; see docs/scope.md");

        var optionMetadata = field.FieldOptionMetadata
            ?? throw new RunnerOutOfScopeException(
                "CodeUnit Metadata (virtual table 2000000137)",
                "codeunit-metadata-virtual-table — \"Subtype\" carries no option metadata; see docs/scope.md");

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = (optionMetadata.OptionString ?? string.Empty).Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            var key = NormalizeObjectTypeName(parts[i]);
            if (key.Length == 0) continue;
            map.TryAdd(key, i);
        }
        if (map.Count == 0)
            throw new RunnerOutOfScopeException(
                "CodeUnit Metadata (virtual table 2000000137)",
                "codeunit-metadata-virtual-table — \"Subtype\" option string is empty; see docs/scope.md");

        _cmvSubtypeOrdinals = map;
        return map;
    }
}
