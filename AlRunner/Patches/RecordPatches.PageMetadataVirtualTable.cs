// RecordPatches.PageMetadataVirtualTable — managed provider for the
// "Page Metadata" (2000000138) system virtual table.
//
// WHY THIS EXISTS (issue #1769)
//   Page Metadata is virtual on the service tier: one row per page compiled into the
//   application, computed from that page's own AL declaration. It routed to the same
//   empty in-memory store as every other table here, so:
//
//     Page Metadata.Get(<any id>)  -> false, always
//
//   That is a silent wrong answer, not an error. Base App "Page Management" (codeunit 700)
//   .GetDefaultCardPageID reads a table's LOOKUP page's CardPageID column off exactly this
//   table (see the "CardPageID IS LOAD-BEARING" note below for the real, verified
//   algorithm — it is not a SourceTable+PageType scan, despite that being a plausible
//   first guess). An empty Page Metadata store made every such Get() fail, so
//   GetDefaultCardPageID silently returned 0 for any table whose lookup page declares a
//   CardPageId, and a direct `PageMetadata.Get(x)` failed outright. See #1720 (Table
//   Metadata) for the sibling table this fix depends on: GetDefaultCardPageID's FIRST step
//   is Table Metadata's LookupPageID column.
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Pages the runner compiles itself — parsed from their AL source
//      (RecordPatches.AlPageParser.cs: Name / SourceTable / PageType / Editable /
//      InsertAllowed / ModifyAllowed / DeleteAllowed / SourceTableTemporary).
//   2. Pages living in a PRECOMPILED dependency (Base Application, System Application,
//      ISV apps) — read from that .app's SymbolReference.json, which states every one of
//      those same properties (BcAppSymbolCache.TryParsePageSymbol). This is the only route
//      for an R2R app: it ships no metadata XML.
//   Source-compiled pages win over symbol-derived ones for the same id — the source is
//   what this run actually compiled.
//
// CardPageID IS LOAD-BEARING, NOT COSMETIC
//   Base App "Page Management".GetDefaultCardPageID does NOT scan Page Metadata by
//   SourceTable+PageType (verified against the actual Base Application 28.1 AL source, in
//   src/Utilities/PageManagement.Codeunit.al — an earlier draft of this fix assumed a scan
//   that does not exist). Its real algorithm is:
//     LookupPageID := Table Metadata[TableID].LookupPageID;
//     if LookupPageID <> 0 then begin
//       PageMetadata.Get(LookupPageID);
//       if PageMetadata.CardPageID <> 0 then exit(PageMetadata.CardPageID);
//     end;
//     exit(0);
//   So resolving a table's default card page requires Page Metadata's OWN CardPageID
//   column on the table's LOOKUP (list) page, not a scan. CardPageID is resolved the same
//   way Table Metadata resolves LookupPageId/DrillDownPageId: the AL/symbol source states
//   it BY NAME (Base Application 28.1's "Customer List" carries
//   CardPageID = "Customer Card"), resolved against the run's own page inventory at
//   row-build time, sharing that inventory with Table Metadata so the two tables can never
//   disagree about which pages exist.
//
// COLUMNS NOT IMPLEMENTED
//   Everything outside Id/Name/Caption/SourceTable/PageType/Editable/InsertAllowed/
//   ModifyAllowed/DeleteAllowed/SourceTableTemporary/CardPageID (API*, DataCaptionExpr.,
//   DelayedInsert, …) gets BC's own NavValue.GetDefaultNavValue for that column's type —
//   the same "declares none of them" default a real row carries for a page that states
//   nothing about them.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider), reached through the same helpers the
//   AllObj / Table Metadata / Report Metadata providers resolve. No AL business-logic body
//   is touched.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int PageMetadataVirtualTableId = 2000000138;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, byte>> _pmvPopulatedByProvider = new();

    private static bool IsPageMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == PageMetadataVirtualTableId;

    /// <summary>One page as Page Metadata exposes it. <see cref="CardPageId"/> is already
    /// resolved to a real page id (0 only when the page genuinely declares none, or when
    /// the declared page could not be resolved — which is reported, never silent; same rule
    /// Table Metadata applies to LookupPageId/DrillDownPageId).</summary>
    private sealed record PageMetaRow(
        int Id, string Name, string Caption, int SourceTableId, string PageType,
        bool Editable, bool InsertAllowed, bool ModifyAllowed, bool DeleteAllowed, bool SourceTableTemporary,
        int CardPageId);

    private static List<PageMetaRow>? _pageMetaRows;
    private static (int Apps, int Parsed) _pageMetaRowsBuiltFrom = (-1, -1);
    private static readonly object _pageMetaRowsLock = new();

    // Resolved once per process from the parsed Page Metadata metatable's own "PageType"
    // field option string — same technique AllObj uses for its "Object Type" ordinals.
    private static Dictionary<string, int>? _pmvPageTypeOrdinals;

    /// <summary>
    /// Populate the in-memory store behind Page Metadata (2000000138) with one row per page
    /// the runner knows about. Idempotent per (provider, page id); called on every handout
    /// so pages registered later in the run still show up.
    /// </summary>
    private static void PopulatePageMetadataVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create(bool)
        EnsureDataAccessProviderReflection(dataAccess);
        var pageTypeOrdinals = EnsurePageTypeOrdinals(metaTable);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Page Metadata (virtual table 2000000138)",
                "page-metadata-virtual-table — data access has no in-memory provider; see docs/scope.md");

        var done = _pmvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        foreach (var row in EnumerateKnownPageMetadata())
        {
            if (!done.TryAdd(row.Id, 0)) continue;
            InsertVirtualRow(provider, metaTable,
                new object[] { PageMetadataVirtualTableId, row.Id, 0, 0 },
                field => BuildPageMetadataValue(field, row, pageTypeOrdinals));
        }
    }

    private static object? BuildPageMetadataValue(NCLMetaField field, PageMetaRow row, Dictionary<string, int> pageTypeOrdinals)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "id":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.Id });
            case "name":
                return Text(row.Name);
            case "caption":
                return Text(row.Caption);
            case "editable":
                return NavBoolean(row.Editable);
            case "pagetype":
                if (pageTypeOrdinals.TryGetValue(NormalizeObjectTypeName(row.PageType), out var ordinal))
                    return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, ordinal });
                // A PageType this BC artifact's option set does not list (should not happen —
                // the compiler validated it against the same enum) — BC's own default rather
                // than a guessed ordinal.
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
            case "sourcetable":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.SourceTableId });
            case "cardpageid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.CardPageId });
            case "insertallowed":
                return NavBoolean(row.InsertAllowed);
            case "modifyallowed":
                return NavBoolean(row.ModifyAllowed);
            case "deleteallowed":
                return NavBoolean(row.DeleteAllowed);
            case "sourcetabletemporary":
                return NavBoolean(row.SourceTableTemporary);
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// Every page the runner has real metadata for: source-parsed pages of the app under
    /// test and of any source-compiled dependency first, then pages declared by the
    /// SymbolReference.json of every registered precompiled dependency .app.
    /// </summary>
    private static List<PageMetaRow> EnumerateKnownPageMetadata()
    {
        var generation = (_bcAppPaths.Count, _parsedPages.Count);
        if (_pageMetaRows != null && _pageMetaRowsBuiltFrom == generation) return _pageMetaRows;
        lock (_pageMetaRowsLock)
        {
            generation = (_bcAppPaths.Count, _parsedPages.Count);
            if (_pageMetaRows != null && _pageMetaRowsBuiltFrom == generation) return _pageMetaRows;

            var rows = new Dictionary<int, PageMetaRow>();
            // Same (name → page id) index Table Metadata resolves LookupPageId/DrillDownPageId
            // against — one shared inventory, so a page name resolvable there is resolvable
            // here too, and the two tables can never disagree about which pages exist.
            var (pageIdsByName, _) = BuildObjectIndexes();
            var unresolvedCardPages = new List<string>();

            int ResolveCardPage(string? name, int pageId)
            {
                if (string.IsNullOrWhiteSpace(name)) return 0;   // declares none — truthful 0
                if (pageIdsByName.TryGetValue(name, out var resolved)) return resolved;
                unresolvedCardPages.Add($"page {pageId} CardPageId -> page '{name}'");
                return 0;
            }

            // 1. Pages the runner source-compiled.
            foreach (var p in _parsedPages.Values)
            {
                rows[p.Id] = new PageMetaRow(
                    p.Id, p.Name,
                    // AL's own default caption is the object name — SourceCaptionFor reads
                    // the Caption property when the AL source declared one.
                    SourceCaptionFor("Page", p.Id) is { Length: > 0 } c ? c : p.Name,
                    GetSourceTableIdForPage(p.Id), p.PageType,
                    p.Editable, p.InsertAllowed, p.ModifyAllowed, p.DeleteAllowed, p.SourceTableTemporary,
                    ResolveCardPage(p.CardPageName, p.Id));
            }

            // 2. Pages declared by precompiled dependency .app packages.
            foreach (var symbol in EnumerateBcAppPageSymbols())
            {
                if (rows.ContainsKey(symbol.Id)) continue;   // source-compiled wins
                rows[symbol.Id] = new PageMetaRow(
                    symbol.Id, symbol.Name, symbol.Caption ?? symbol.Name,
                    symbol.SourceTableId, symbol.PageType,
                    symbol.Editable, symbol.InsertAllowed, symbol.ModifyAllowed, symbol.DeleteAllowed,
                    symbol.SourceTableTemporary, ResolveCardPage(symbol.CardPageName, symbol.Id));
            }

            if (unresolvedCardPages.Count > 0)
                Console.Error.WriteLine(
                    $"[RecordPatches] Page Metadata: {unresolvedCardPages.Count} declared CardPageId reference(s) "
                    + "could not be resolved to a page id and are reported as 0: "
                    + string.Join("; ", unresolvedCardPages.Take(10))
                    + (unresolvedCardPages.Count > 10 ? $" (+{unresolvedCardPages.Count - 10} more)" : string.Empty));

            _pageMetaRows = rows.Values.ToList();
            _pageMetaRowsBuiltFrom = generation;
            return _pageMetaRows;
        }
    }

    private static IEnumerable<BcAppSymbolCache.PageSymbol> EnumerateBcAppPageSymbols()
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.PageSymbol> pages;
            try
            {
                pages = BcAppSymbolCache.Get(appPath).Pages;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] Page Metadata: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var p in pages)
                yield return p;
        }
    }

    /// <summary>
    /// Read the "PageType" option field's ordinals out of the parsed Page Metadata
    /// metatable's own NCLOptionMetadata.OptionString, matched by name — never a hardcoded
    /// table, so the mapping tracks whatever the System package in the resolved artifact
    /// declares. Mirrors AllObj's EnsureAllObjObjectTypeOrdinals for its "Object Type" column.
    /// </summary>
    private static Dictionary<string, int> EnsurePageTypeOrdinals(NCLMetaTable metaTable)
    {
        if (_pmvPageTypeOrdinals != null) return _pmvPageTypeOrdinals;

        var field = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => NormalizeObjectTypeName(f.FieldName ?? string.Empty) == "pagetype")
            ?? throw new RunnerOutOfScopeException(
                "Page Metadata (virtual table 2000000138)",
                "page-metadata-virtual-table — metatable has no \"PageType\" field; see docs/scope.md");

        var optionMetadata = field.FieldOptionMetadata
            ?? throw new RunnerOutOfScopeException(
                "Page Metadata (virtual table 2000000138)",
                "page-metadata-virtual-table — \"PageType\" carries no option metadata; see docs/scope.md");

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
                "Page Metadata (virtual table 2000000138)",
                "page-metadata-virtual-table — \"PageType\" option string is empty; see docs/scope.md");

        _pmvPageTypeOrdinals = map;
        return map;
    }
}
