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
//   yet) and flipping it for every page later is not desirable: pages living in a
//   precompiled dependency have no captured XML, and forcing a load for them would turn a
//   currently-harmless skeleton into a hard RunnerOutOfScopeException from the loader.
//   So the load is requested by the one caller that actually needs a control tree — the
//   TestPage path — and only for pages the runner compiled itself.
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
    /// Ensure <paramref name="pageId"/>'s NCLMetaForm carries its real, parsed page
    /// definition, and return it. Returns null when the runner has no emit-captured
    /// metadata XML for the page (a precompiled dependency's page) or when the load
    /// failed — in both cases the caller must not pretend it has a control tree.
    /// Idempotent: the load runs at most once per page per run.
    /// </summary>
    internal static object? EnsureRealPageMetadata(int pageId)
    {
        if (!AlPageMetadataRegistry.TryGet(pageId, out _)) return null;

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
