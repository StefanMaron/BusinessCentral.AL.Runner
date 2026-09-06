// RecordPatches.DependencyPageMetadata — SourceTable lookup for pages that live in a
// PRECOMPILED dependency .app, which the runner never source-compiles.
//
// THE GAP (issue #1719)
//   NavFormHandle.CreateTarget (a plain `Page X` variable, as opposed to a TestPage) needs
//   to bind Rec to a real record of the page's own SourceTable before handing the instance
//   to AL — otherwise any Base App/System App page method that reads Rec (e.g. Page 700
//   "Error Messages".SetRecords: `Rec.Copy(TempErrorMessage, true)`) NREs before AL ever
//   runs. RecordPatches.GetSourceTableIdForPage answers that ONLY for a page the runner
//   AL-source-parsed itself (AlPageParser scans `_sourceDirs`, which is the bundle's own
//   .al files) — a precompiled dependency's page has no entry there at all.
//
// WHAT IS RECONSTRUCTED, AND FROM WHAT
//   The dependency .app's own SymbolReference.json states the page's SourceTable property
//   verbatim as the table's numeric ID (see BcAppSymbolCache.TryParsePageSymbol) — this is
//   the same file DependencyReportMetadata already reads for a dependency report's dataset
//   shape, so nothing new is inferred here, only a second typed slice of the same source.
namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// A precompiled dependency's SourceTable table id for <paramref name="pageId"/>, or 0
    /// when no loaded dependency .app describes that page (or the page declares no
    /// SourceTable at all — a legal AL page with no bound record).
    /// </summary>
    internal static int TryGetDependencySourceTableIdForPage(int pageId)
        => TryGetDependencyPageSymbol(pageId)?.SourceTableId ?? 0;

    /// <summary>
    /// <paramref name="pageId"/>'s SourceTable table id, checking the runner's own
    /// AL-source-parsed pages first, then any loaded dependency .app's SymbolReference.json.
    /// 0 when neither knows the page or the page declares no SourceTable.
    /// </summary>
    internal static int ResolveSourceTableIdForAnyPage(int pageId)
    {
        var tableId = GetSourceTableIdForPage(pageId);
        return tableId != 0 ? tableId : TryGetDependencySourceTableIdForPage(pageId);
    }

    /// <summary>
    /// Whether the runner knows <paramref name="pageId"/>'s DECLARED SHAPE at all — from its
    /// own AL source parse, or from a loaded dependency .app's SymbolReference.json.
    ///
    /// <para>This is the predicate every "is 'declares no SourceTable' a fact about the page,
    /// or about our ignorance?" decision actually wants. <see cref="IsPageParsed"/> answers
    /// only the first half, and reading it as the whole question is what issue #2341 was:
    /// TestPage 9807 "User Card" ships in the precompiled Base Application, whose
    /// SymbolReference.json states <c>SourceTable = 2000000120</c> verbatim, and the runner
    /// refused to resolve it while already holding the answer.</para>
    /// </summary>
    internal static bool IsPageShapeKnown(int pageId)
        => IsPageParsed(pageId) || TryGetDependencyPageSymbol(pageId) != null;

    /// <summary>
    /// Whether <paramref name="pageId"/> declares a SourceTable — checking the runner's own
    /// AL-source-parsed pages first, then any loaded dependency .app.
    ///
    /// <para>Source-compiled wins outright for a page id the parser saw, mirroring how the
    /// Page Metadata / Page Control Field virtual tables merge the two sources: a parsed page
    /// that declares none must answer false, never fall through to a same-numbered dependency
    /// page's answer.</para>
    /// </summary>
    internal static bool ResolvePageDeclaresSourceTableForAnyPage(int pageId)
        => IsPageParsed(pageId)
            ? PageDeclaresSourceTable(pageId)
            : (TryGetDependencyPageSymbol(pageId)?.SourceTableId ?? 0) != 0;

    /// <summary>
    /// Whether <paramref name="pageId"/> declares <c>SourceTableTemporary = true</c> —
    /// checking the runner's own AL-source-parsed pages first, then any loaded dependency
    /// .app. False (including "unknown page") is the safe default — it is also AL's own
    /// default, so a page the runner cannot find gets exactly the record shape a page with
    /// no such declaration would. See issue #1719: Page 700 "Error Messages" declares it
    /// true, and its own SetRecords body's <c>Rec.Copy(TempErrorMessage, true)</c> requires
    /// a temporary Rec to match.
    /// </summary>
    internal static bool ResolveSourceTableTemporaryForAnyPage(int pageId)
        => (IsPageParsed(pageId) && _parsedPages.TryGetValue(pageId, out var page) && page.SourceTableTemporary)
           || TryGetDependencyPageSymbol(pageId)?.SourceTableTemporary == true;

    /// <summary>
    /// #3143: NOT swallowed. This is the highest-leverage of the ten sites, because almost
    /// every caller reads it as `TryGetDependencyPageSymbol(id)?.X ?? default` — so a read
    /// that could not answer used to produce `InsertAllowed = true`, `SourceTableId = 0`,
    /// `PageType = null`, `IsPageKnown = false`. Those are not missing answers, they are
    /// wrong ones, and no AL-visible signal distinguished them. Now shares the one walk with
    /// the pageextension lookups below; see RecordPatches.DependencyAppSymbolWalk.cs.
    /// </summary>
    private static BcAppSymbolCache.PageSymbol? TryGetDependencyPageSymbol(int pageId)
    {
        foreach (var symbols in DependencyAppSymbols())
            foreach (var p in symbols.Pages)
                if (p.Id == pageId)
                    return p;
        return null;
    }

    /// <summary>
    /// The precompiled dependency <c>pageextension</c> with object id
    /// <paramref name="extensionId"/>, or null when no loaded dependency .app declares one
    /// (issue #2723's pageextension arm). Same walk as <see cref="TryGetDependencyPageSymbol"/>,
    /// same failure handling: a .app that has VANISHED from disk since registration is
    /// skipped with a <c>[warn]</c> Log's default verbosity shows, and a .app that is
    /// present but whose SymbolReference cannot be read RAISES
    /// <see cref="BcAppSymbolReadException"/> — neither is treated as "declares none". See
    /// RecordPatches.DependencyAppSymbolWalk.cs for why those two are not one condition.
    /// </summary>
    private static BcAppSymbolCache.PageExtensionSymbol? TryGetDependencyPageExtensionSymbol(int extensionId)
    {
        foreach (var symbols in DependencyAppSymbols())
            foreach (var ext in symbols.PageExtensions ?? (IReadOnlyList<BcAppSymbolCache.PageExtensionSymbol>)Array.Empty<BcAppSymbolCache.PageExtensionSymbol>())
                if (ext.Id == extensionId)
                    return ext;
        return null;
    }

    /// <summary>
    /// Object ids of every precompiled dependency <c>pageextension</c> whose target page NAME
    /// matches <paramref name="basePageName"/> (space-insensitive, case-insensitive — the same
    /// <c>NamesEqual</c> rule the AL-source-parsed extensions are matched with). Feeds
    /// <see cref="GetPageExtensionIdsForPage"/>; the caller dedupes against the source-parsed
    /// set, where a same-numbered source-parsed extension wins.
    /// </summary>
    private static IEnumerable<int> DependencyPageExtensionIdsForPage(string basePageName)
    {
        foreach (var symbols in DependencyAppSymbols())
            foreach (var ext in symbols.PageExtensions ?? (IReadOnlyList<BcAppSymbolCache.PageExtensionSymbol>)Array.Empty<BcAppSymbolCache.PageExtensionSymbol>())
                if (NamesEqual(ext.TargetObjectName, basePageName))
                    yield return ext.Id;
    }

    /// <summary>
    /// Every loaded dependency .app's parsed symbols, in registration order, so the page and
    /// pageextension lookups share one walk and one failure policy.
    ///
    /// <para>#3143: this used to swallow EVERY read failure and <c>continue</c>, so a .app the
    /// runner could not read reported "declares no pages" — a wrong answer rather than a
    /// missing one, whose only trace was a `[RecordPatches]`-tagged line Log's default filter
    /// drops. The two conditions are now separated: a VANISHED .app is skipped on `[warn]`,
    /// and one that is present but unreadable raises
    /// <see cref="BcAppSymbolReadException"/>. See RecordPatches.DependencyAppSymbolWalk.cs.</para>
    /// </summary>
    private static IEnumerable<BcAppSymbolCache.AppSymbols> DependencyAppSymbols()
    {
        foreach (var (_, symbols) in
                 EnumerateRegisteredBcAppSymbols("pages and pageextensions (dependency page metadata)"))
            yield return symbols;
    }

    /// <summary>
    /// Numeric field id for <paramref name="fieldName"/> on <paramref name="tableId"/>
    /// (issue #2467 — resolving a dependency part's SubPageLink field names to the numbers
    /// BC's own compiled metadata carries). Reuses the SAME table-symbol machinery
    /// RecordPatches.BcAppFallback.cs already builds for FlowField CalcFormula source-table
    /// resolution (_parsedTables / TryPopulateParsedTableFromBcApps) — this is a second
    /// caller of it, not new lookup infrastructure. Null when the table or field is unknown.
    /// </summary>
    internal static int? TryResolveDependencyFieldId(int tableId, string fieldName)
    {
        if (tableId <= 0 || string.IsNullOrWhiteSpace(fieldName)) return null;
        if (!_parsedTables.TryGetValue(tableId, out var table))
        {
            TryPopulateParsedTableFromBcApps(tableId);
            _parsedTables.TryGetValue(tableId, out table);
        }
        // GetAllFieldsIncludingExtensions, not table.Fields alone — see #2490: a SubPageLink
        // field name may be one a tableextension added, same as a page control's binding.
        var field = table == null ? null : GetAllFieldsIncludingExtensions(table).FirstOrDefault(f =>
            string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
        return field?.FieldId;
    }
}
