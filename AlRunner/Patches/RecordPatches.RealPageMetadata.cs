// RecordPatches.RealPageMetadata — opt a single page's NCLMetaForm into a REAL metadata
// load, on demand.
//
// WHY ON DEMAND
//   BuildNCLMetaForm runs at Register() time — before the compile that captures page
//   metadata XML — and force-sets metadataLoaded = true so BC's Populate() path stays
//   skipped. That flag is what makes every page a control-less skeleton: it tells BC
//   "already loaded", so LoadMetadata() never runs.
//
//   Flipping it for every page at build time is not possible (the metadata does not exist
//   yet) and flipping it for every page unconditionally is not desirable: a page neither
//   the runner nor any loaded dependency describes still has no XML to load, and forcing an
//   attempt for it would turn a currently-harmless skeleton into a hard
//   RunnerOutOfScopeException from the loader. So the load is requested by callers that
//   actually need real PageProperties — the TestPage path, and (#1939)
//   RunnerFormInit.ShouldResolveMasterPage on AL's own `Page.RunModal()` — gated on the
//   page having SOME source of real metadata: the runner's own emit-captured XML
//   (AlPageMetadataRegistry) or a loaded dependency .app's SymbolReference.json
//   (HasDependencyPageMetadata — see DependencyPageMetadataXml.cs).
//
// WHAT A REAL LOAD BUYS
//   NCLMetaForm.LoadMetadata() -> LoadPageMetadata() -> CreatePageDefinitionWithExtensions()
//   -> ObjectLoader.MetaObjectCache.GetMetaPage(id, appGroup) parses the emit-captured XML
//   into a MetaPageDefinition: the page's real control tree, with each control's id and
//   what it is bound to. That tree is what NavForm registers its source expressions from,
//   and therefore the only thing that can resolve a control bound to a page VARIABLE
//   rather than to a Rec field.
//
// FAILURE POLICY
//   A page we have XML for that nonetheless fails to load is a runner gap, not something
//   to paper over: the caller is told (null) and reports it loudly rather than silently
//   continuing with a skeleton, which would answer TestPage questions wrongly instead of
//   refusing to answer them.
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly HashSet<int> _pagesWithRealMetadata = new();

    // #3011. The page-side NEGATIVE answers, each stamped with the .app registration epoch
    // (BcAppRegistrationEpoch — RecordPatches.BcAppFallback.cs) it was taken at.
    //
    // There are exactly two of them and they now share one record, deliberately:
    //   * BuildNCLMetaForm returned null — no NCLMetaForm could be built for the id at all;
    //   * LoadMetadata() threw — a metaform exists but its real metadata would not load.
    // Both are derived from the registered .app set. HasDependencyPageMetadata walks
    // _bcAppPaths, and BuildNCLMetaForm's own existence check calls it; the load itself
    // reaches TryBuildDependencyPageMetadata, the memo #2889/#2944 fixed. So both answers
    // are true only for as long as that set holds still, and answering from either one
    // after the set has grown is the same defect #2888 fixed on the report side
    // (NavReportSync._realMetaCache): a second-order memo that keeps serving a null taken
    // before a registration, masking the first-order fix for its own consumer.
    //
    // Why an epoch stamp instead of a clear at the InvalidateBcAppIndexes funnel — the shape
    // #2888 used for every other instance, and the one #3011's body suggests:
    //   1. LOCK ORDER. EnsureRealPageMetadata holds _realPageMetadataLock across
    //      LoadMetadata(), which reaches BC code that takes _bcTableIndexLock (table metadata
    //      for the page's source table). InvalidateBcAppIndexes runs while HOLDING
    //      _bcTableIndexLock, so taking _realPageMetadataLock there would invert the order
    //      against a live load and is a deadlock, not an inefficiency.
    //   2. COST. AddBcAppPath runs once per dependency .app. A clear at the funnel does the
    //      work N times per bundle whether or not anything ever failed; the stamp does it
    //      lazily, once per (page, epoch), and only for a page that actually got a negative
    //      answer — normally none at all.
    //   3. #1957's invariant. A retry may not re-run LoadMetadata() on the instance the
    //      failed attempt already mutated, so the retake evicts that instance first
    //      (EvictPageMetaForm) rather than clearing a set and hoping.
    //
    // The SUCCESS set above is deliberately NOT stamped and NOT revalidated. It records that
    // one specific live NCLMetaForm has had its real metadata loaded into it, which a later
    // registration does not falsify — and re-running the load on it is exactly what #1957
    // forbids. Its lifetime is still governed by ResetPageMetadataForReload, because a bundle
    // roll discards the instances it describes; the epoch cannot substitute for that.
    private static readonly Dictionary<int, int> _pageRealMetadataNegativeEpoch = new();

    // Per-page count of "the registration set moved, so the negative answer was taken again".
    // Test-visible bookkeeping in production code for the same reason
    // PopulateNclMetadataCacheCallCount is: the claim #3011's proving test makes is about a
    // DECISION (was the question re-asked, and exactly once per epoch), and a count is the
    // only way to state it that a no-op implementation cannot satisfy. Written only on the
    // retake path, so it stays empty for every page that never got a negative answer.
    private static readonly Dictionary<int, int> _pageRealMetadataRetakes = new();

    private static readonly object _realPageMetadataLock = new();

    /// <summary>
    /// The registration epoch at which <see cref="EnsureRealPageMetadata"/> last answered
    /// null for <paramref name="pageId"/>, or null when it holds no negative answer for that
    /// page — either because it never gave one, or because the answer has since been retaken
    /// against a newer epoch. See <c>_pageRealMetadataNegativeEpoch</c> (#3011).
    /// </summary>
    internal static int? PageRealMetadataNegativeEpochForTests(int pageId)
    {
        lock (_realPageMetadataLock)
            return _pageRealMetadataNegativeEpoch.TryGetValue(pageId, out var e) ? e : null;
    }

    /// <summary>
    /// How many times a negative real-page-metadata answer for <paramref name="pageId"/> has
    /// been retaken because the .app registration set changed under it (#3011). Zero for a
    /// page that never got a negative answer, and for one whose negative answer has never
    /// been revisited.
    /// </summary>
    internal static int PageRealMetadataRetakesForTests(int pageId)
    {
        lock (_realPageMetadataLock)
            return _pageRealMetadataRetakes.TryGetValue(pageId, out var n) ? n : 0;
    }

    /// <summary>Whether <paramref name="pageId"/> is recorded as having had its REAL page
    /// metadata successfully loaded — the success set #1957 requires
    /// <see cref="ResetPageMetadataForReload"/> to discard alongside the
    /// <c>NCLMetaForm</c> instances it describes.</summary>
    internal static bool PageHasRealMetadataForTests(int pageId)
    {
        lock (_realPageMetadataLock) return _pagesWithRealMetadata.Contains(pageId);
    }

    /// <summary>
    /// Clear the "already loaded" / "already failed" bookkeeping alongside
    /// <c>_metaFormCache</c> on a <c>--watch</c> reload (#1957).
    /// <para>
    /// Both sets are statements about ONE specific <c>NCLMetaForm</c> instance — "this
    /// object's <c>metadataLoaded</c> flag has been cleared and <c>LoadMetadata()</c> has
    /// run on it" (or "was attempted and threw"). <see cref="ResetForReload"/> discards
    /// exactly those instances via <c>_metaFormCache.Clear()</c>; leaving either set
    /// populated makes <see cref="EnsureRealPageMetadata"/> answer questions about a
    /// generation of <c>NCLMetaForm</c> objects that no longer exist.
    /// </para>
    /// <para>
    /// The success set surviving meant the NEXT lookup short-circuited past a brand-new,
    /// never-loaded skeleton as "already loaded" — BC then dereferenced a page definition
    /// that was never parsed (NRE out of
    /// <c>GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage</c>), and
    /// <c>TestPage</c>'s catch-and-fall-back silently downgraded to record-only access, so
    /// <c>OnOpenPage</c> quietly stopped running from the second cycle onward.
    /// </para>
    /// <para>
    /// The negative record is cleared for the mirror reason, not merely for symmetry: a
    /// page that could not load against the previous generation must get a fresh attempt
    /// against this one, or an edit that fixes the underlying cause could never be
    /// observed to have fixed it. This runs once per <c>--watch</c> cycle, so a page whose
    /// metadata load genuinely, repeatedly fails pays for one retry per cycle — not a
    /// retry storm — and still logs loudly on every failed attempt
    /// (<see cref="EnsureRealPageMetadata"/>'s catch block), so a real gap stays visible
    /// rather than being silently swallowed by either generation's cache.
    /// </para>
    /// <para>
    /// #3011 makes the negative half redundant on this path — the reload bumps
    /// <see cref="BcAppRegistrationEpoch"/> through <c>InvalidateBcAppIndexes</c>, so every
    /// stamped negative answer is stale by then anyway — and it stays here regardless,
    /// because the SUCCESS set is not epoch-stamped and cannot be: this clear is the only
    /// thing that discards it, and #1957's NRE is what happens when it does not.
    /// </para>
    /// </summary>
    internal static void ResetPageMetadataForReload()
    {
        lock (_realPageMetadataLock)
        {
            _pagesWithRealMetadata.Clear();
            _pageRealMetadataNegativeEpoch.Clear();
            _pageRealMetadataRetakes.Clear();
        }
    }

    /// <summary>
    /// Ensure <paramref name="pageId"/>'s NCLMetaForm carries its real, parsed page
    /// definition, and return it. Returns null when the runner has no emit-captured
    /// metadata XML for the page (a precompiled dependency's page) or when the load
    /// failed — in both cases the caller must not pretend it has a control tree.
    /// Idempotent: the load runs at most once per page per run (per --watch cycle — see
    /// <see cref="ResetForReload"/>), and a NEGATIVE answer is retaken at most once per .app
    /// registration epoch (#3011 — see <c>_pageRealMetadataNegativeEpoch</c>).
    /// </summary>
    internal static object? EnsureRealPageMetadata(int pageId)
    {
        if (!AlPageMetadataRegistry.TryGet(pageId, out _) && !HasDependencyPageMetadata(pageId)) return null;

        // Read once, outside the lock: an int read is atomic and the epoch only ever moves
        // forward, so a concurrent bump can at worst make this call record its negative
        // answer against the older epoch — which the next call then retakes. The opposite
        // (a stale answer surviving a bump) is the defect, and cannot happen this way.
        var epoch = BcAppRegistrationEpoch;

        lock (_realPageMetadataLock)
        {
            object? meta;
            if (_pageRealMetadataNegativeEpoch.TryGetValue(pageId, out var negativeAt))
            {
                // Same registration set the negative answer was taken against: it still
                // stands, and re-deriving it would be a retry storm on the TestPage path.
                if (negativeAt == epoch) return null;

                // The set has changed. Discard everything the negative attempt left behind
                // and take the question again — exactly once for this epoch, because the
                // record below is rewritten with the new epoch if the answer is still no.
                //
                // The metaform goes with it (#1957): a failed LoadMetadata() leaves the
                // instance half-mutated, and re-running LoadMetadata() on that same object
                // is precisely what must not happen. EvictPageMetaForm drops it from
                // _metaFormCache AND from BC's own metadataCacheEntries[Page], so the two
                // cannot disagree about which instance is page N's.
                _pageRealMetadataNegativeEpoch.Remove(pageId);
                _pageRealMetadataRetakes[pageId] =
                    (_pageRealMetadataRetakes.TryGetValue(pageId, out var n) ? n : 0) + 1;
                EvictPageMetaForm(pageId);

                meta = _metaFormCache.GetOrAdd(pageId, BuildNCLMetaForm);
                if (meta != null) RefreshPageInMetadataCache(pageId, meta);
            }
            else
            {
                meta = _metaFormCache.GetOrAdd(pageId, BuildNCLMetaForm);
            }

            if (meta == null)
            {
                // No NCLMetaForm could be built. That answer is derived from the registered
                // .app set too — BuildNCLMetaForm's existence check calls
                // HasDependencyPageMetadata — so it is stamped rather than memoized forever.
                // _metaFormCache memoizes the null itself, which is why the retake above has
                // to evict it before asking again.
                _pageRealMetadataNegativeEpoch[pageId] = epoch;
                return null;
            }

            if (_pagesWithRealMetadata.Contains(pageId)) return meta;

            try
            {
                // Clear the "already loaded" flag BuildNCLMetaForm set, so BC's own
                // LoadMetadata() actually runs instead of returning immediately.
                EnsureCachePopulatorReflection();
                if (_fNCLMetaAppObjMetadataLoaded != null)
                    AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, false);

                meta.GetType()
                    .GetMethod("LoadMetadata", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(meta, null);

                _pagesWithRealMetadata.Add(pageId);
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                    Console.Out.WriteLine($"[page-metadata] loaded real metadata for page {pageId}");
                return meta;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                // Same record, same stamp, same guard as the "no metaform" branch above —
                // one mechanism for both negative answers, so neither can be covered while
                // the other is not (#3011).
                _pageRealMetadataNegativeEpoch[pageId] = epoch;
                // Put the flag back so the skeleton behaves exactly as it did before the
                // attempt — a half-loaded metaform is worse than none.
                if (_fNCLMetaAppObjMetadataLoaded != null)
                    AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);
                Console.Error.WriteLine(
                    $"[RecordPatches] page {pageId}: real metadata load failed ({inner.GetType().Name}: {inner.Message}); "
                    + "falling back to the control-less skeleton");
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                    Console.Out.WriteLine(
                        $"[page-metadata] page {pageId} LoadMetadata THREW {inner.GetType().Name}: {inner.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Drop page <paramref name="pageId"/>'s <c>NCLMetaForm</c> from BOTH layers that can
    /// serve it: the runner's own <c>_metaFormCache</c> and the skeleton NCLMetadata's
    /// <c>metadataCacheEntries[Page]</c> dictionary <see cref="PopulateNclMetadataCache"/>
    /// fills from it.
    ///
    /// <para>Both, not one (#3011). <c>PopulateOneObjectType</c> wraps the very object
    /// <c>_metaFormCache</c> holds in an <c>NCLMetadataCacheEntry</c> and stores it in BC's
    /// dictionary, so the two layers hold the SAME instance by reference — which is what
    /// makes <see cref="EnsureRealPageMetadata"/>'s in-place load visible to BC at all.
    /// Evicting only the runner's copy and rebuilding would hand out a second, different
    /// <c>NCLMetaForm</c> for one page id while BC's dictionary kept the first: our own
    /// <c>NCLMetadata_GetMetaApplicationObjectByType</c> hook would answer with the new one
    /// and anything reading the dictionary with the old one. Same eviction shape, and the
    /// same reasoning, as <c>TddReparseAndRefreshTable</c> on the table side.</para>
    ///
    /// <para>Best-effort on the BC half: if the reflection handles are unavailable the entry
    /// stays, and the caller's rebuild is then a no-op against BC's view rather than a
    /// divergence, because <see cref="RefreshPageInMetadataCache"/> puts the fresh instance
    /// back under the same key.</para>
    /// </summary>
    private static void EvictPageMetaForm(int pageId)
    {
        _metaFormCache.TryRemove(pageId, out _);
        TryMutatePageMetadataCacheEntries(dict => dict.Remove(pageId));
    }

    /// <summary>
    /// Put <paramref name="meta"/> back into the skeleton NCLMetadata's
    /// <c>metadataCacheEntries[Page]</c> under <paramref name="pageId"/>, so BC's dictionary
    /// and <c>_metaFormCache</c> agree on which <c>NCLMetaForm</c> is that page's after a
    /// <see cref="EvictPageMetaForm"/> + rebuild (#3011).
    ///
    /// <para>Assigns rather than TryAdds, and that is the difference from
    /// <c>PopulateOneObjectType</c>: this runs precisely when a stale entry may still be
    /// there, so skipping an existing key would reinstate exactly the divergence the
    /// eviction exists to prevent. <c>metadataLoaded</c> is set true first, matching what
    /// the populator does with a freshly built skeleton — the caller flips it back to false
    /// for the load attempt that follows.</para>
    /// </summary>
    private static void RefreshPageInMetadataCache(int pageId, object meta)
    {
        if (_mCreateWithBase == null) { EnsureCachePopulatorReflection(); }
        if (_mCreateWithBase == null) return;
        try
        {
            if (_fNCLMetaAppObjMetadataLoaded != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);
            var entry = _mCreateWithBase.Invoke(null, new object?[] { meta });
            if (entry != null) TryMutatePageMetadataCacheEntries(dict => dict[pageId] = entry);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine(
                $"[RecordPatches] page {pageId}: could not refresh the NCLMetadata cache entry "
                + $"({inner.GetType().Name}: {inner.Message})");
        }
    }

    /// <summary>Run <paramref name="mutate"/> against the skeleton NCLMetadata's
    /// <c>metadataCacheEntries[Page]</c> dictionary, or do nothing when there is no skeleton
    /// / the reflection handles did not resolve. Never throws: a failure here leaves BC's
    /// own lookup to raise its ordinary not-found, which is loud, rather than turning a
    /// cache-maintenance problem into an unrelated exception mid-load.</summary>
    private static void TryMutatePageMetadataCacheEntries(Action<System.Collections.IDictionary> mutate)
    {
        try
        {
            var skeleton = BcRuntime.SkeletonNCLMetadata;
            if (skeleton == null) return;
            EnsureCachePopulatorReflection();
            if (_fNCLMetadataCacheEntries == null) return;
            if (_fNCLMetadataCacheEntries.GetValue(skeleton) is not Array arr) return;
            const int objectTypePage = 8;
            if (arr.Length <= objectTypePage) return;
            if (arr.GetValue(objectTypePage) is not System.Collections.IDictionary dict) return;
            mutate(dict);
        }
        catch
        {
            // Best-effort, exactly as TddReparseAndRefreshTable's own eviction is.
        }
    }
}
