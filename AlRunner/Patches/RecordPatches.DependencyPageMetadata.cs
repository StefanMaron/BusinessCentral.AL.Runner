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
    /// <param name="surface">
    /// What the CALLER was reading, passed through to <see cref="EnumerateRegisteredBcAppSymbols"/>
    /// so a <see cref="BcAppSymbolReadException"/> raised while the index is being built names the
    /// caller's own surface. Memoizing under one fixed surface would have made every caller's
    /// refusal say "pages and pageextensions (dependency page metadata)" — see the memo's remarks.
    /// </param>
    private static BcAppSymbolCache.PageSymbol? TryGetDependencyPageSymbol(
        int pageId, string surface = "pages and pageextensions (dependency page metadata)")
        => DependencyPageSymbolsById(surface).TryGetValue(pageId, out var page) ? page : null;

    // #3774 — page id -> PageSymbol, memoized per registration epoch. The walk above ran ONCE
    // PER CALL and DependencyObjectSubtype calls it once per enumerated `page` object, so the
    // cost was O(objects x apps) file stats. Measurements: PR #4223.
    //
    // TRAP 1: the key is the EPOCH, never _bcAppPaths.Count — the registered set shrinks as
    // well as grows, so a count cannot tell a set that lost N entries and gained N different
    // ones from the one it was built against (#2888's ABA case). InvalidateBcAppIndexes bumps
    // the epoch unconditionally as its last statement (RecordPatches.BcAppFallback.cs), and
    // both registration funnels pass through it.
    //
    // TRAP 2: the epoch is the ONLY term, deliberately. Do NOT add _parsedPages.Count — the
    // source-parsed pages are a different source this lookup does not consult (callers check
    // IsPageParsed first), so that term would invalidate the memo on every bundle page parse
    // for an index those pages never enter.
    //
    // TRAP 3: do not "simplify" this by having BcAppSymbolCache.Get consult ProcessCache by
    // path to skip the stat. Get keys ProcessCache on the CONTENT HASH and
    // RunnerFingerprint.ComputeFileContentHashMemoized keys on statx/(path,length,mtime), so
    // the stat IS the key: keying on the path alone would replay symbols for an .app whose
    // bytes changed underneath it. This fix stops ASKING rather than weakening the answer.
    private static Dictionary<int, BcAppSymbolCache.PageSymbol>? _dependencyPageSymbolsById;
    private static int _dependencyPageSymbolsBuiltFromEpoch = -1;
    private static readonly object _dependencyPageSymbolsLock = new();

    /// <summary>
    /// Page id → the precompiled dependency <see cref="BcAppSymbolCache.PageSymbol"/> declaring
    /// it, over the same walk <see cref="DependencyAppSymbols"/> performs, memoized per
    /// registration epoch (#3774). Handed out SHARED and read-only: a caller that mutated it
    /// would corrupt every later lookup.
    ///
    /// <para>FIRST WINS for a page id two registered .apps both declare, which is the order the
    /// per-call walk answered in — <see cref="EnumerateRegisteredBcAppSymbols"/> yields in
    /// registration order and the old loop returned on its first match, so preserving it keeps
    /// this a pure memoization rather than a behaviour change. Reachable because AddBcAppPath
    /// dedupes on the PATH only, so two .app files may each declare page N; pinned by
    /// DependencyPageSymbolIndexMemoTests.WhenTwoAppsDeclareTheSamePageId_TheFirstRegisteredWins,
    /// which reverses to Expected 111 / Actual 222 if this order is dropped.</para>
    /// </summary>
    private static Dictionary<int, BcAppSymbolCache.PageSymbol> DependencyPageSymbolsById(
        string surface = "pages and pageextensions (dependency page metadata)")
    {
        var epoch = BcAppRegistrationEpoch;
        if (_dependencyPageSymbolsById is { } memo && _dependencyPageSymbolsBuiltFromEpoch == epoch)
            return memo;

        lock (_dependencyPageSymbolsLock)
        {
            epoch = BcAppRegistrationEpoch;
            if (_dependencyPageSymbolsById is { } inner && _dependencyPageSymbolsBuiltFromEpoch == epoch)
                return inner;

            DependencyPageSymbolIndexBuildCountForTests++;
            var index = new Dictionary<int, BcAppSymbolCache.PageSymbol>();
            // The caller's surface, NOT DependencyAppSymbols()'s fixed one: an unreadable .app
            // must raise BcAppSymbolReadException naming what the CALLER was reading, and this
            // build now happens underneath whichever walk asked first. Pinned by
            // DependencySymbolReadFailureTests.SymbolReadFailsAfterRegistration_* — which caught
            // exactly this regression when the memo first landed (see PR #4223).
            foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols(surface))
                foreach (var p in symbols.Pages)
                    // FIRST wins — see the summary above. TryAdd, not the indexer.
                    index.TryAdd(p.Id, p);

            // Reached only when the walk COMPLETED: EnumerateRegisteredBcAppSymbols throws out of
            // the loop above on an unreadable .app, so a partial index is never published and the
            // next caller retries rather than inheriting a short answer. That is the refusal
            // DependencySymbolReadFailureTests asserts is repeatable, not one-shot.
            //
            // Index before stamp, both inside the lock, so a torn read cannot pair a NEW stamp
            // with an OLDER index. Neither field is volatile, so this orders the writes rather
            // than guaranteeing what an unsynchronized reader observes: the fast path may read a
            // stale pair and rebuild, which costs a walk and never a wrong answer. Same shape as
            // the ~10 sibling memos keyed on this epoch (Volatile.Read on the epoch itself).
            _dependencyPageSymbolsById = index;
            _dependencyPageSymbolsBuiltFromEpoch = epoch;
            return index;
        }
    }

    /// <summary>
    /// How many times the registered .apps have actually been WALKED for the page-symbol index,
    /// as opposed to answered from the memo. A COUNT, never a duration — #3774's proving test
    /// asserts that N lookups at one epoch cost one walk, which is the whole claim, and a
    /// duration assertion could not distinguish a memo from a fast disk.
    /// </summary>
    internal static int DependencyPageSymbolIndexBuildCountForTests { get; private set; }

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
