// RecordPatches.TableMetadataVirtualTable — managed provider for the
// "Table Metadata" (2000000136) system virtual table.
//
// WHY THIS EXISTS
//   Table Metadata is virtual on the service tier: its rows are computed from the
//   metadata of every table in the application. It routed to the same empty
//   in-memory store as every other table here, so:
//
//     Table Metadata.Get(<any id>)  -> false, always
//
//   That is a silent wrong answer, not an error. AL takes its not-found branch and
//   reports something misleading one level up, and the damage is not confined to AL
//   that reads the table directly: Base Application "Page Management" (codeunit 700)
//   resolves a record's lookup/card page through it, so GetPageID / PageRun /
//   PageRunModal on a custom table died with
//     NavCSideRecordNotFoundException: The Table Metadata does not exist.
//     Identification fields and values: ID='50150'
//   inside GetDefaultLookupPageID, even for a table plainly declaring LookupPageId.
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Tables the runner compiles itself — parsed from their AL source
//      (RecordPatches.AlSourceParser.cs: name, TableType, DataPerCompany and the
//      LookupPageId / DrillDownPageId references).
//   2. Tables living in a PRECOMPILED dependency (Base Application, System
//      Application, ISV apps) — read from that .app's SymbolReference.json
//      (BcAppSymbolCache.TryParseTableSymbol). This is the only route for an R2R
//      app: it ships no metadata XML.
//   Source-compiled tables win over symbol-derived ones for the same id — the source
//   is what this run actually compiled.
//
//   Captions come from the same inventory AllObjWithCaption uses
//   (EnumerateKnownAlObjects), so a table's caption here and its caption there cannot
//   drift apart.
//
// PAGE IDS ARE RESOLVED, NOT GUESSED
//   Both sources state the page BY NAME — AL source writes the reference, and
//   SymbolReference.json records LookupPageID/LookupPageId as the page's name (measured
//   against Base Application 28.1; note both spellings occur). So the name is resolved
//   against the run's own page inventory at row-build time.
//
//   0 is a MEANINGFUL value in these two columns: it is how Page Management recognises
//   "this table declares no lookup/drilldown page" and falls through to its next rule.
//   So a table that DOES declare one whose page the runner cannot resolve must not be
//   handed out as 0 — that would assert something false about the table. Such a table
//   keeps its other columns and reports the unresolved page as 0 only after the miss is
//   counted in a single stderr line naming the tables affected, so the gap is visible
//   rather than silently answered.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider), reached through the same helpers the
//   AllObj provider resolves. No AL business-logic body is touched.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2): one store-wiring refusal, on a table this file populates.
    /// </remarks>
    internal static RunnerOutOfScopeException TableMetadataShapeGap(string detail)
        => VirtualTableShapeGap("Table Metadata (virtual table 2000000136)", "table-metadata-virtual-table", detail);

    internal const int TableMetadataVirtualTableId = 2000000136;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, byte>> _tmvPopulatedByProvider = new();

    private static bool IsTableMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == TableMetadataVirtualTableId;

    /// <summary>
    /// One table as Table Metadata exposes it. <see cref="LookupPageId"/> /
    /// <see cref="DrillDownPageId"/> are already resolved to real page ids (0 only when
    /// the table genuinely declares none, or when the declared page could not be
    /// resolved — which is reported, never silent).
    /// </summary>
    /// <param name="TableTypeName">The declared <c>TableType</c> as written, or null for a table
    /// declaring none (AL's default, <c>Normal</c>). Resolved to the column's own option ordinal
    /// at row-build time, never to a hardcoded number — see <see cref="EnsureOptionOrdinals"/>.</param>
    /// <param name="DataClassificationName">Same, for <c>DataClassification</c>; null means AL's
    /// default <c>CustomerContent</c>.</param>
    /// <param name="ExternalName">The declared <c>ExternalName</c>, or null for a table declaring
    /// none — which the column reports as blank.</param>
    private sealed record TableMetadataRow(
        int Id, string Name, string Caption, bool DataPerCompany, bool IsTemporary,
        int LookupPageId, int DrillDownPageId,
        string? TableTypeName, string? DataClassificationName, string? ExternalName);

    // Cached inventory, rebuilt whenever the runner has learned about more source-parsed
    // tables or registered another dependency .app since the last build. The FIRST handout
    // can happen before the bundle's dependencies are registered, and a snapshot taken then
    // would permanently hide every Base Application table — the same trap the Report
    // Metadata provider documents.
    private static List<TableMetadataRow>? _tableMetadataRows;
    private static (int Apps, int Parsed, int Pages) _tableMetadataRowsBuiltFrom = (-1, -1, -1);
    private static readonly object _tableMetadataRowsLock = new();

    /// <summary>
    /// Populate the in-memory store behind Table Metadata (2000000136) with one row per
    /// table the runner knows about. Idempotent per (provider, table id); called on every
    /// handout so tables registered later in the run still show up.
    /// </summary>
    private static void PopulateTableMetadataVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create(bool)
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw TableMetadataShapeGap("data access has no in-memory provider");

        var done = _tmvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        foreach (var row in EnumerateKnownTableMetadata())
        {
            if (!done.TryAdd(row.Id, 0)) continue;
            InsertVirtualRow(provider, metaTable,
                new object[] { TableMetadataVirtualTableId, row.Id, 0, 0 },
                field => BuildTableMetadataValue(field, row));
        }
    }

    /// <summary>
    /// One column of a Table Metadata row, matched by the metatable's own FIELD NAME so the
    /// mapping tracks whatever the System package in the resolved artifact declares rather
    /// than a hardcoded field-number table. Columns the runner cannot answer truthfully
    /// (ObsoleteState/Reason, ReplicateData, ExtensionID, TableType beyond temporary, …) get
    /// BC's own default — which is also what a real row carries for a table declaring none
    /// of them.
    /// </summary>
    private static object? BuildTableMetadataValue(NCLMetaField field, TableMetadataRow row)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        // An option column answered from the DECLARED member name, resolved against this
        // field's own option set. A table declaring nothing gets ordinal 0, which is what AL's
        // own default is for both option columns here (TableType = Normal,
        // DataClassification = CustomerContent) — so the fallback states the truth rather than
        // hiding a miss. A declared name the option set does not list is a genuine shape
        // mismatch and is refused, not defaulted: answering 0 there would say "Normal" about a
        // table that declared something else, the exact silent-default this change removes.
        object? Option(string? declaredName)
        {
            var ordinals = EnsureOptionOrdinals(field);
            if (string.IsNullOrWhiteSpace(declaredName)) return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, 0 });
            if (ordinals.TryGetValue(NormalizeObjectTypeName(declaredName), out var ordinal))
                return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, ordinal });
            throw TableMetadataShapeGap(
                $"table {row.Id} declares {field.FieldName} = '{declaredName}', which is not a member of "
                + $"that column's own option set ('{field.FieldOptionMetadata?.OptionString}')");
        }

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "id":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.Id });
            case "name":
                return Text(row.Name);
            case "caption":
                return Text(row.Caption);
            case "lookuppageid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.LookupPageId });
            case "drilldownpageid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.DrillDownPageId });
            case "datapercompany":
                return NavBoolean(row.DataPerCompany);
            // NOTE: BC 28.1's own Table Metadata carries NO "TemporaryTable" column — the
            // temporary-ness of a table is reported through TableType below. This case is kept
            // for an artifact that does declare one; it is not the route the corpus exercises.
            case "temporarytable":
                return NavBoolean(row.IsTemporary);
            // The three columns of #2938. Each used to fall through to the default branch and
            // answer its type's zero value — Normal, CustomerContent, blank — for EVERY table,
            // so a temporary table, a CRM table and a plain one were indistinguishable.
            case "tabletype":
                // IsTableTypeTemporary is the older two-valued view of the same AL property and
                // is the only thing the symbol path recorded before TableTypeName existed; it is
                // consulted only when no name was captured, so a v29-era cached ParsedTable
                // still reports Temporary rather than silently reading Normal.
                return Option(row.TableTypeName ?? (row.IsTemporary ? "Temporary" : null));
            case "dataclassification":
                return Option(row.DataClassificationName);
            case "externalname":
                return Text(row.ExternalName ?? string.Empty);
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    // One ordinal map per option COLUMN of this metatable, keyed by the field name. Two columns
    // need one here (TableType, DataClassification), and the metatable is built once per run.
    private static readonly ConcurrentDictionary<string, Dictionary<string, int>> _tmvOptionOrdinals = new();

    /// <summary>
    /// The ordinals of an option column, read out of that field's OWN
    /// <c>NCLOptionMetadata.OptionString</c> and keyed by normalized member name — never a
    /// hardcoded table, so the mapping tracks whatever the System package in the resolved
    /// artifact declares. Mirrors Page Metadata's <c>EnsurePageTypeOrdinals</c> and CodeUnit
    /// Metadata's <c>EnsureCodeunitSubtypeOrdinals</c>, which answer the same question for their
    /// own single option column.
    /// <para>Measured on BC 28.1.49838.53910, this is what those two option strings say — and
    /// the reason the ordinals are read rather than written down: <c>TableType</c> is
    /// <c>Normal,CRM,ExternalSQL,Exchange,MicrosoftGraph,Query,Temporary</c>, so Temporary is 6
    /// and NOT the 5 a reading of AL's documented enum would suggest, and
    /// <c>DataClassification</c> is
    /// <c>CustomerContent,ToBeClassified,EndUserIdentifiableInformation,AccountData,EndUserPseudonymousIdentifiers,OrganizationIdentifiableInformation,SystemMetadata</c>,
    /// which puts <c>ToBeClassified</c> second and SystemMetadata at 6 rather than 5.</para>
    /// </summary>
    private static Dictionary<string, int> EnsureOptionOrdinals(NCLMetaField field)
    {
        var fieldName = field.FieldName ?? string.Empty;
        return _tmvOptionOrdinals.GetOrAdd(fieldName, _ =>
        {
            var optionMetadata = field.FieldOptionMetadata
                ?? throw TableMetadataShapeGap($"\"{fieldName}\" carries no option metadata");

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var parts = (optionMetadata.OptionString ?? string.Empty).Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                var key = NormalizeObjectTypeName(parts[i]);
                if (key.Length == 0) continue;
                map.TryAdd(key, i);
            }
            if (map.Count == 0)
                throw TableMetadataShapeGap($"\"{fieldName}\" option string is empty");
            return map;
        });
    }

    /// <summary>
    /// Every table the runner has real metadata for: source-parsed tables of the app under
    /// test and of any source-compiled dependency first, then tables declared by the
    /// SymbolReference.json of every registered precompiled dependency .app.
    /// </summary>
    private static List<TableMetadataRow> EnumerateKnownTableMetadata()
    {
        var generation = (_bcAppPaths.Count, _parsedTables.Count, _parsedPages.Count);
        if (_tableMetadataRows != null && _tableMetadataRowsBuiltFrom == generation) return _tableMetadataRows;
        lock (_tableMetadataRowsLock)
        {
            generation = (_bcAppPaths.Count, _parsedTables.Count, _parsedPages.Count);
            if (_tableMetadataRows != null && _tableMetadataRowsBuiltFrom == generation) return _tableMetadataRows;

            // Built once and closed over below rather than through ResolvePageReference's own
            // (also-correct, but per-call) lookup — this loop calls into page resolution twice
            // per table (LookupPageId + DrillDownPageId), and re-running BuildObjectIndexes'
            // full object-inventory scan that often would undo the whole-run caching this
            // method exists for.
            var (pageIdsByName, tableCaptionsById) = BuildObjectIndexes();
            var unresolvedPages = new List<string>();

            // A page reference as written: a bare id stays that id, otherwise the run's own
            // page inventory answers. An unresolvable NAME is reported, never quietly 0 — see
            // ResolvePageReference's doc comment for why 0 is otherwise a meaningful, truthful
            // answer and not something to distinguish here.
            int ResolvePage(string? reference, int tableId, string propertyName)
            {
                if (string.IsNullOrWhiteSpace(reference)) return 0;   // declares none — truthful 0
                if (int.TryParse(reference, out var literal) && literal > 0) return literal;
                if (pageIdsByName.TryGetValue(reference, out var id)) return id;
                unresolvedPages.Add($"table {tableId} {propertyName} -> page '{reference}'");
                return 0;
            }

            var rows = new Dictionary<int, TableMetadataRow>();

            TableMetadataRow Build(ParsedTable t) => new(
                t.TableId,
                t.TableName,
                // AL's own default caption is the object name — applied once, here.
                tableCaptionsById.TryGetValue(t.TableId, out var c) && c.Length > 0 ? c : t.TableName,
                t.DataPerCompany,
                t.IsTableTypeTemporary,
                ResolvePage(t.LookupPageName, t.TableId, "LookupPageId"),
                ResolvePage(t.DrillDownPageName, t.TableId, "DrillDownPageId"),
                t.TableTypeName,
                t.DataClassificationName,
                t.ExternalName);

            // 1. Tables the runner source-compiled.
            foreach (var parsed in _parsedTables.Values)
                rows[parsed.TableId] = Build(parsed);

            // 2. Tables declared by precompiled dependency .app packages.
            foreach (var symbol in EnumerateBcAppTableSymbols())
            {
                if (rows.ContainsKey(symbol.TableId)) continue;   // source-compiled wins
                rows[symbol.TableId] = Build(symbol);
            }

            if (unresolvedPages.Count > 0)
                Console.Error.WriteLine(
                    $"[RecordPatches] Table Metadata: {unresolvedPages.Count} declared page reference(s) "
                    + "could not be resolved to a page id and are reported as 0; Page Management will treat "
                    + "those tables as declaring no page: "
                    + string.Join("; ", unresolvedPages.Take(10))
                    + (unresolvedPages.Count > 10 ? $" (+{unresolvedPages.Count - 10} more)" : string.Empty));

            _tableMetadataRows = rows.Values.ToList();
            _tableMetadataRowsBuiltFrom = generation;

            var trace = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_TABLE_METADATA");
            if (!string.IsNullOrEmpty(trace))
            {
                Console.Out.WriteLine(
                    $"[table-metadata] {_tableMetadataRows.Count} table(s) known "
                    + $"({_parsedTables.Count} source-parsed, {_bcAppPaths.Count} dependency .app(s))");
                if (int.TryParse(trace, out var probeId) && probeId > 1)
                    Console.Out.WriteLine(rows.TryGetValue(probeId, out var probe)
                        ? $"[table-metadata] {probeId}: name='{probe.Name}' caption='{probe.Caption}' "
                          + $"lookupPage={probe.LookupPageId} drillDownPage={probe.DrillDownPageId} "
                          + $"dataPerCompany={probe.DataPerCompany} temporary={probe.IsTemporary}"
                        : $"[table-metadata] {probeId}: NOT KNOWN");
            }
            return _tableMetadataRows;
        }
    }

    /// <summary>
    /// Two indexes off ONE pass over the object inventory AllObj/AllObjWithCaption publish:
    /// page name → page id, and table id → declared caption. Sharing that inventory is what
    /// keeps a page listed there resolvable here, and a table's caption here identical to
    /// its caption there — including for tables that live in a precompiled dependency,
    /// whose captions only the symbol file states.
    /// <para>Page names are compared case-insensitively, as AL itself compares object names.
    /// The FIRST id wins for a duplicated name, matching the inventory's own source
    /// precedence (source-compiled before dependency symbols).</para>
    /// </summary>
    private static (Dictionary<string, int> PageIdsByName, Dictionary<int, string> TableCaptionsById) BuildObjectIndexes()
    {
        var pages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var captions = new Dictionary<int, string>();
        foreach (var (kind, id, name, caption) in EnumerateKnownAlObjects())
        {
            if (id <= 0 || string.IsNullOrEmpty(name)) continue;
            switch (NormalizeObjectTypeName(kind))
            {
                case "page":
                    pages.TryAdd(name, id);
                    break;
                case "table":
                    if (!string.IsNullOrEmpty(caption)) captions.TryAdd(id, caption);
                    break;
            }
        }
        return (pages, captions);
    }

    /// <summary>
    /// Resolves a page reference as WRITTEN in an AL table property (<c>LookupPageId</c> /
    /// <c>DrillDownPageId</c>) — either a bare id in text form or a page NAME — to a page
    /// object id, against this run's own page inventory (<see cref="BuildObjectIndexes"/>).
    /// Shared by the Table Metadata (2000000136) virtual-table row builder above and by
    /// <c>RecordPatches.NclMetaTableBuilder.cs</c>'s <c>NCLMetaTable.LookupFormId</c>
    /// population (#1918) — both read the identical AL-declared property and must agree on
    /// what it resolves to.
    /// <para>Returns 0 when <paramref name="reference"/> is null/blank (the table declares
    /// none — a meaningful, truthful 0, not a failure) or when a named reference cannot be
    /// resolved against the known page inventory. Callers that need to distinguish "declares
    /// none" from "declares an unresolvable name" — as the Table Metadata row builder does,
    /// to report the gap — must do that check themselves before calling in, exactly as its
    /// local <c>ResolvePage</c> does.</para>
    /// </summary>
    internal static int ResolvePageReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return 0;
        if (int.TryParse(reference, out var literal) && literal > 0) return literal;
        var (pageIdsByName, _) = BuildObjectIndexes();
        return pageIdsByName.TryGetValue(reference, out var id) ? id : 0;
    }

    private static IEnumerable<ParsedTable> EnumerateBcAppTableSymbols()
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<ParsedTable> tables;
            try
            {
                tables = BcAppSymbolCache.Get(appPath).Tables;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] Table Metadata: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var t in tables)
                yield return t;
        }
    }
}
