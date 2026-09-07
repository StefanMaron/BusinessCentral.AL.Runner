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
//   Twenty of the table's 32 columns are answered here. Eleven come off PageMetaRow
//   (Id/Name/Caption/SourceTable/PageType/Editable/InsertAllowed/ModifyAllowed/
//   DeleteAllowed/SourceTableTemporary/CardPageID); the nine <SourceObject> ones
//   (SourceTableView/DelayedInsert/ShowFilter/MultipleNewLines/SaveValues/AutoSplitKey/
//   DataCaptionFields/LinksAllowed/PopulateAllFields) were added by #3063 and are read from
//   BC's OWN parsed page metadata — see RecordPatches.PageMetadataSourceObject.cs, which is
//   also where the refusal policy for them lives.
//
//   The remaining twelve still get BC's own NavValue.GetDefaultNavValue for the column's
//   type — the same "declares none of them" default a real row carries for a page that
//   states nothing about them. They are DataCaptionExpr., RefreshOnActivate, APIPublisher,
//   APIGroup, APIVersion, EntitySetName, EntityName, ChangeTrackingAllowed, AppID,
//   InherentPermissions, InherentEntitlements and Namespace. Unlike the nine above, none of
//   these has a value sitting parsed and unused in the process today: the API* / Entity* /
//   DataCaptionExpr. / RefreshOnActivate / ChangeTrackingAllowed group is <Properties>-level
//   rather than <SourceObject>-level and is not carried on either row source, and the last
//   four are computed by BC from an app-identity and permission-mask surface the runner does
//   not populate at all. Each is therefore a separate piece of work, not a fall-through this
//   file could close by widening its switch.
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
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2) for all four. One is a store-wiring gap; the other three are BC metadata
    /// shapes this file reads rather than owns. Refusing beats guessing an option ordinal: the
    /// ordinal is a stored column value, so a wrong guess mis-keys every row it writes and no
    /// test can see it.
    /// </remarks>
    internal static RunnerOutOfScopeException PageMetadataShapeGap(string detail)
        => VirtualTableShapeGap("Page Metadata (virtual table 2000000138)", "page-metadata-virtual-table", detail);

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
    // The .app term is RecordPatches' registration EPOCH, never _bcAppPaths.Count (#2888):
    // the registered set can SHRINK since #2755 / PR #2873, so a count cannot tell a set that
    // lost N entries and gained N different ones from the one it was built against — and in
    // --watch mode (same bundle, one edited file) that is the NORMAL case, not a corner. The
    // remaining terms stay counts and are sound as counts, because the dictionaries they count
    // are only ever cleared by ResetForReload, which bumps the epoch in the same breath.
    private static (int Epoch, int Parsed) _pageMetaRowsBuiltFrom = (-1, -1);
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
            ?? throw PageMetadataShapeGap("data access has no in-memory provider");

        var done = _pmvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        foreach (var row in EnumerateKnownPageMetadata())
        {
            if (!done.TryAdd(row.Id, 0)) continue;

            // One lazy slot per ROW, shared by that row's nine <SourceObject> columns (#3063).
            // The read behind it loads the page's real metadata through BC's own
            // LoadMetadata(); nine columns asking independently would ask nine times for an
            // answer that cannot differ between them. Lazy rather than eager because most
            // rows are never asked for any of the nine, and a page whose metadata will not
            // load must refuse only when something actually reads one of its columns — not
            // take the whole table's population down with it.
            PageSourceObjectInfo? sourceObject = null;
            PageSourceObjectInfo SourceObjectFor(PageMetaRow r) => sourceObject ??= GetPageSourceObject(r.Id);

            InsertVirtualRow(provider, metaTable,
                new object[] { PageMetadataVirtualTableId, row.Id, 0, 0 },
                field => BuildPageMetadataValue(field, row, pageTypeOrdinals, SourceObjectFor));
        }
    }

    private static object? BuildPageMetadataValue(
        NCLMetaField field, PageMetaRow row, Dictionary<string, int> pageTypeOrdinals,
        Func<PageMetaRow, PageSourceObjectInfo> SourceObjectFor)
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
                return _aovNavOptionCreate!.Invoke(null, new object?[]
                {
                    field.FieldOptionMetadata,
                    ResolvePageTypeOrdinal(
                        pageTypeOrdinals, field.FieldOptionMetadata?.OptionString, row.PageType, row.Id)
                });
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

            // The nine <SourceObject> columns (#3063). Read from BC's own parsed page
            // metadata — the same MetaSourceObjectDefinition BC's real PageDataProvider reads
            // them off — so a source-compiled page and a page from a dependency .app cannot
            // answer differently. See RecordPatches.PageMetadataSourceObject.cs, which also
            // states which of them refuses rather than defaulting and why.
            //
            // Resolved lazily and ONCE per row build, not per column: the read reaches
            // EnsureRealPageMetadata, which loads the page's real metadata through BC's own
            // LoadMetadata() on first use. Nine columns of one row would otherwise ask nine
            // times for an answer that cannot change between them.
            case "sourcetableview":
                return Text(SourceObjectFor(row).SourceTableView);
            case "delayedinsert":
                return NavBoolean(SourceObjectFor(row).DelayedInsert);
            case "showfilter":
                return NavBoolean(SourceObjectFor(row).ShowFilter);
            case "multiplenewlines":
                return NavBoolean(SourceObjectFor(row).MultipleNewLines);
            case "savevalues":
                return NavBoolean(SourceObjectFor(row).SaveValues);
            case "autosplitkey":
                return NavBoolean(SourceObjectFor(row).AutoSplitKey);
            case "datacaptionfields":
                return Text(SourceObjectFor(row).DataCaptionFields);
            case "linksallowed":
                return NavBoolean(SourceObjectFor(row).LinksAllowed);
            case "populateallfields":
                return NavBoolean(SourceObjectFor(row).PopulateAllFields);

            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// AL's default <c>PageType</c> for a page that declares none. Both row sources already
    /// substitute it before a row is built — <c>ParsePages</c> and
    /// <c>BcAppSymbolCache.TryParsePageSymbol</c> each write "Card" for an absent property —
    /// so this is the backstop for a null that slips past them (a symbol payload cached by an
    /// older schema version, say), and the one place the substitution is stated as a MEMBER
    /// NAME resolved against the live column rather than as a position.
    /// </summary>
    private const string AlDefaultPageType = "Card";

    /// <summary>
    /// The ordinal Page Metadata's <c>PageType</c> column carries for one page: the declared
    /// member when the page states the property, <see cref="AlDefaultPageType"/> when it does
    /// not, and a refusal when neither the column's option string nor BC's own
    /// <c>PageType</c> enum knows the name.
    /// <para>Both misses are refused rather than defaulted (#3080). Before this, a name in
    /// neither fell through to <c>NavValue.GetDefaultNavValue</c> — ordinal 0 — under a comment
    /// claiming the case could not arise because "the compiler validated it against the same
    /// enum". The compiler does; the COLUMN does not list everything the compiler accepts. See
    /// RecordPatches.MetadataOptionEnumOrdinals.cs: BC 28.1's option string stops at
    /// HeadlinePart, while the enum runs on to PromptDialog (20) and UserControlHost (22), and
    /// Base Application 28.1 ships a page of each. Both were answered "Card".</para>
    /// <para>Split out of <c>BuildPageMetadataValue</c> so the resolution can be driven with an
    /// option string and an enum the caller chooses; the row-build path holds live BC metatable
    /// objects a unit test cannot construct, and the case that matters is precisely the one
    /// where the two sources DISAGREE, which no single artifact can present.</para>
    /// </summary>
    /// <param name="ordinals">Member name (normalized) to ordinal, from
    /// <see cref="EnsurePageTypeOrdinals"/> or <see cref="BuildMetadataOptionOrdinals(string?, Type?)"/>.</param>
    /// <param name="optionString">The column's own option string, for the refusal message.</param>
    /// <param name="declaredPageType">What the page declares, or null/blank when it declares nothing.</param>
    /// <param name="pageId">The page, for the refusal message.</param>
    internal static int ResolvePageTypeOrdinal(
        IReadOnlyDictionary<string, int> ordinals, string? optionString, string? declaredPageType, int pageId)
    {
        if (string.IsNullOrWhiteSpace(declaredPageType))
        {
            if (ordinals.TryGetValue(NormalizeObjectTypeName(AlDefaultPageType), out var defaultOrdinal))
                return defaultOrdinal;
            throw PageMetadataShapeGap(
                $"page {pageId} declares no PageType, and the default for it ('{AlDefaultPageType}') is "
                + $"neither a member of that column's own option set ('{optionString}') nor of BC's own "
                + $"{BcPageTypeEnumName}");
        }
        if (ordinals.TryGetValue(NormalizeObjectTypeName(declaredPageType), out var ordinal))
            return ordinal;
        throw PageMetadataShapeGap(
            $"page {pageId} declares PageType = '{declaredPageType}', which is neither a member of that "
            + $"column's own option set ('{optionString}') nor of BC's own {BcPageTypeEnumName}");
    }

    /// <summary>
    /// Every page the runner has real metadata for: source-parsed pages of the app under
    /// test and of any source-compiled dependency first, then pages declared by the
    /// SymbolReference.json of every registered precompiled dependency .app.
    /// </summary>
    private static List<PageMetaRow> EnumerateKnownPageMetadata()
    {
        var generation = (BcAppRegistrationEpoch, _parsedPages.Count);
        if (_pageMetaRows != null && _pageMetaRowsBuiltFrom == generation) return _pageMetaRows;
        lock (_pageMetaRowsLock)
        {
            generation = (BcAppRegistrationEpoch, _parsedPages.Count);
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

    // The same rows EnumerateKnownPageMetadata builds, keyed by page id, so the by-id lookups
    // below are not a linear scan of every page in the run (Base Application 28.1 alone
    // contributes several thousand). Rebuilt whenever the row list itself is rebuilt —
    // reference equality against the cached list is the cheapest correct invalidation, since
    // EnumerateKnownPageMetadata already owns the generation check.
    private static List<PageMetaRow>? _pageMetaRowsIndexedFrom;
    private static Dictionary<int, PageMetaRow>? _pageMetaRowsById;
    private static readonly object _pageMetaIndexLock = new();

    /// <summary>
    /// One page's resolved Page Metadata row, or null when neither the runner's own parsed AL
    /// nor a registered dependency .app's SymbolReference.json declares that page.
    /// </summary>
    private static PageMetaRow? TryGetPageMetaRow(int pageId)
    {
        var rows = EnumerateKnownPageMetadata();
        var index = _pageMetaRowsById;
        if (!ReferenceEquals(_pageMetaRowsIndexedFrom, rows) || index == null)
            lock (_pageMetaIndexLock)
            {
                if (!ReferenceEquals(_pageMetaRowsIndexedFrom, rows) || _pageMetaRowsById == null)
                {
                    var built = new Dictionary<int, PageMetaRow>(rows.Count);
                    foreach (var row in rows) built[row.Id] = row;
                    _pageMetaRowsById = built;
                    _pageMetaRowsIndexedFrom = rows;
                }
                index = _pageMetaRowsById;
            }
        return index.TryGetValue(pageId, out var found) ? found : null;
    }

    /// <summary>
    /// <paramref name="pageId"/>'s <c>CardPageId</c>, already resolved from the declared NAME
    /// to a real page id — 0 when the page declares none, when the declared page is not in this
    /// run's inventory (reported by <see cref="EnumerateKnownPageMetadata"/>, never silent), or
    /// when the page itself is unknown here.
    ///
    /// <para>Issue #3185's consumer is <c>LiveNavTestPage.View()/Edit()</c>: the built-in
    /// page-mode actions a client puts on a list open exactly this page, and
    /// <c>ActionBuilder.ResolveCardFormId</c> (BC 28.1's own UI builder) reads it from the same
    /// declaration Page Metadata reports here.</para>
    /// </summary>
    internal static int TryGetAnyCardPageId(int pageId) => TryGetPageMetaRow(pageId)?.CardPageId ?? 0;

    /// <summary>
    /// Whether <paramref name="pageId"/> allows modification — its declared
    /// <c>ModifyAllowed</c> narrowed by its declared <c>Editable</c>. Null when the page is not
    /// in this run's inventory, which a caller must not read as either answer.
    /// </summary>
    internal static bool? TryGetAnyPageModifyAllowed(int pageId)
        => TryGetPageMetaRow(pageId) is { } row ? row.Editable && row.ModifyAllowed : null;

    /// <summary>
    /// #3143: NOT swallowed — an unreadable dependency used to leave every page it declares
    /// out of Page Metadata (2000000138) AND out of Page Control Field, the other consumer of
    /// this walk. See RecordPatches.DependencyAppSymbolWalk.cs.
    /// </summary>
    private static IEnumerable<BcAppSymbolCache.PageSymbol> EnumerateBcAppPageSymbols()
    {
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("pages (Page Metadata)"))
            foreach (var p in symbols.Pages)
                yield return p;
    }

    /// <summary>
    /// Read the "PageType" option field's ordinals out of the parsed Page Metadata
    /// metatable's own NCLOptionMetadata.OptionString, matched by name — never a hardcoded
    /// table, so the mapping tracks whatever the System package in the resolved artifact
    /// declares. Mirrors AllObj's EnsureAllObjObjectTypeOrdinals for its "Object Type" column.
    /// <para>The option string is not the only source, and it is not the authoritative one:
    /// BC's PageDataProvider writes GetOptionValue(5, (int)properties.PageType), the ordinal of
    /// its OWN PageType enum, which reaches past the last member this column names. See
    /// RecordPatches.MetadataOptionEnumOrdinals.cs for the measurement and for why the enum
    /// wins any name the two share (#3080).</para>
    /// </summary>
    private static Dictionary<string, int> EnsurePageTypeOrdinals(NCLMetaTable metaTable)
    {
        if (_pmvPageTypeOrdinals != null) return _pmvPageTypeOrdinals;

        var field = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => NormalizeObjectTypeName(f.FieldName ?? string.Empty) == "pagetype")
            ?? throw PageMetadataShapeGap("metatable has no \"PageType\" field");

        var optionMetadata = field.FieldOptionMetadata
            ?? throw PageMetadataShapeGap("\"PageType\" carries no option metadata");

        var map = BuildMetadataOptionOrdinals(
            optionMetadata.OptionString, BcPageTypeEnumName, "Page Metadata \"PageType\"");
        if (map.Count == 0)
            throw PageMetadataShapeGap("\"PageType\" option string is empty");

        _pmvPageTypeOrdinals = map;
        return map;
    }
}
