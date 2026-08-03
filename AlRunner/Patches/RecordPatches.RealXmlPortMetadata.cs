// RecordPatches.RealXmlPortMetadata — opt a single xmlport's NCLMetaXmlPort into a REAL
// metadata load, on demand. Direct sibling of RecordPatches.RealPageMetadata.cs; the same
// ordering constraint and the same failure policy apply, for the same reasons.
//
// WHY ON DEMAND
//   BuildNCLMetaXmlPort runs at Register() time — before the compile that captures xmlport
//   metadata XML — and force-sets metadataLoaded = true so BC's Populate() path stays
//   skipped. That flag is exactly what leaves the port schema-less, so every real xmlport
//   operation NREs: NCLMetaXmlPort.CreateObjectInstance has no node tree to instantiate and
//   NCLMetaApplicationObject.GetMetadataFromLoader has nothing to read.
//
//   Flipping it for every xmlport at build time is impossible (the metadata does not exist
//   yet); flipping it later for every xmlport is wrong, because an xmlport living in a
//   precompiled dependency has no captured XML and forcing a load would turn a harmless
//   skeleton into a hard RunnerOutOfScopeException from the loader. So the load is
//   requested on first metadata lookup, and only for xmlports the runner compiled itself.
//
// WHAT A REAL LOAD BUYS
//   NCLMetaXmlPort.LoadMetadata() parses the emit-captured XML into a real MetaXmlPort —
//   the port's node schema, its table/text elements and attributes, and the AL trigger
//   bindings. With it, BC's OWN XmlPort engine performs the import/export. That is the
//   whole point: run MS's serializer, don't reimplement it.
//
// FAILURE POLICY
//   An xmlport we have XML for that nonetheless fails to load is a runner gap, not
//   something to paper over: the caller is told (null) and the skeleton is restored
//   exactly as it was, so behaviour never silently degrades into "wrong answer" territory.
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly HashSet<int> _xmlPortsWithRealMetadata = new();
    private static readonly HashSet<int> _xmlPortsRealMetadataFailed = new();
    private static readonly object _realXmlPortMetadataLock = new();

    /// <summary>
    /// Ensure <paramref name="xmlPortId"/>'s NCLMetaXmlPort carries its real, parsed
    /// definition, and return it. Returns null when the runner has no emit-captured
    /// metadata XML for the xmlport (a precompiled dependency's port) or when the load
    /// failed. Idempotent: the load runs at most once per xmlport per run.
    /// </summary>
    internal static object? EnsureRealXmlPortMetadata(int xmlPortId)
    {
        if (!AlXmlPortMetadataRegistry.TryGet(xmlPortId, out _)) return null;

        var meta = _metaXmlPortCache.GetOrAdd(xmlPortId, BuildNCLMetaXmlPort);
        if (meta == null) return null;

        lock (_realXmlPortMetadataLock)
        {
            if (_xmlPortsRealMetadataFailed.Contains(xmlPortId)) return null;
            if (_xmlPortsWithRealMetadata.Contains(xmlPortId)) return meta;

            try
            {
                // Clear the "already loaded" flag BuildNCLMetaXmlPort set, so BC's own
                // LoadMetadata() actually runs instead of returning immediately.
                EnsureCachePopulatorReflection();
                if (_fNCLMetaAppObjMetadataLoaded != null)
                    AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, false);

                meta.GetType()
                    .GetMethod("LoadMetadata", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(meta, null);

                _xmlPortsWithRealMetadata.Add(xmlPortId);
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_XMLPORT_METADATA") == "1")
                    Console.Out.WriteLine($"[xmlport-metadata] loaded real metadata for xmlport {xmlPortId}");
                return meta;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                _xmlPortsRealMetadataFailed.Add(xmlPortId);
                // Put the flag back so the skeleton behaves exactly as it did before the
                // attempt — a half-loaded metaxmlport is worse than none.
                if (_fNCLMetaAppObjMetadataLoaded != null)
                    AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);
                Console.Error.WriteLine(
                    $"[RecordPatches] xmlport {xmlPortId}: real metadata load failed "
                    + $"({inner.GetType().Name}: {inner.Message}); falling back to the schema-less skeleton");
                return null;
            }
        }
    }
}
