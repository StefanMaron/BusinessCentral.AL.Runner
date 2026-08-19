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
    private static readonly HashSet<int> _pagesRealMetadataFailed = new();
    private static readonly object _realPageMetadataLock = new();

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
    /// The failure set is cleared for the mirror reason, not merely for symmetry: a page
    /// that could not load against the previous generation must get a fresh attempt
    /// against this one, or an edit that fixes the underlying cause could never be
    /// observed to have fixed it. This runs once per <c>--watch</c> cycle, so a page whose
    /// metadata load genuinely, repeatedly fails pays for one retry per cycle — not a
    /// retry storm — and still logs loudly on every failed attempt
    /// (<see cref="EnsureRealPageMetadata"/>'s catch block), so a real gap stays visible
    /// rather than being silently swallowed by either generation's cache.
    /// </para>
    /// </summary>
    internal static void ResetPageMetadataForReload()
    {
        lock (_realPageMetadataLock)
        {
            _pagesWithRealMetadata.Clear();
            _pagesRealMetadataFailed.Clear();
        }
    }

    /// <summary>
    /// Ensure <paramref name="pageId"/>'s NCLMetaForm carries its real, parsed page
    /// definition, and return it. Returns null when the runner has no emit-captured
    /// metadata XML for the page (a precompiled dependency's page) or when the load
    /// failed — in both cases the caller must not pretend it has a control tree.
    /// Idempotent: the load runs at most once per page per run (per --watch cycle — see
    /// <see cref="ResetForReload"/>).
    /// </summary>
    internal static object? EnsureRealPageMetadata(int pageId)
    {
        if (!AlPageMetadataRegistry.TryGet(pageId, out _) && !HasDependencyPageMetadata(pageId)) return null;

        var meta = _metaFormCache.GetOrAdd(pageId, BuildNCLMetaForm);
        if (meta == null) return null;

        lock (_realPageMetadataLock)
        {
            if (_pagesRealMetadataFailed.Contains(pageId)) return null;
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
                _pagesRealMetadataFailed.Add(pageId);
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
}
