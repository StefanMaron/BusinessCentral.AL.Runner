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
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Both sets are statements about ONE generation of NCLMetaXmlPort instances — the ones
    // _metaXmlPortCache holds — so ResetXmlPortMetadataForReload discards them alongside it
    // (#3172; the page-side equivalent is #1957's ResetPageMetadataForReload). The GROW
    // direction #3011 fixed on the page side is a SEPARATE and still-unverified question
    // here, and is deliberately NOT addressed: this file's gate reads
    // AlXmlPortMetadataRegistry and _parsedXmlPorts, neither of which is derived from the
    // registered .app set, so the page side's epoch argument does not transfer unexamined.
    private static readonly HashSet<int> _xmlPortsWithRealMetadata = new();
    private static readonly HashSet<int> _xmlPortsRealMetadataFailed = new();
    private static readonly object _realXmlPortMetadataLock = new();

    /// <summary>Whether <paramref name="xmlPortId"/> is recorded as having had its REAL
    /// xmlport metadata successfully loaded — the success set #3172 requires
    /// <see cref="ResetXmlPortMetadataForReload"/> to discard alongside the
    /// <c>NCLMetaXmlPort</c> instances it describes.</summary>
    internal static bool XmlPortHasRealMetadataForTests(int xmlPortId)
    {
        lock (_realXmlPortMetadataLock) return _xmlPortsWithRealMetadata.Contains(xmlPortId);
    }

    /// <summary>Whether <paramref name="xmlPortId"/> is recorded as having FAILED its real
    /// metadata load. The mirror of <see cref="XmlPortHasRealMetadataForTests"/>, and the
    /// half that suppresses every later attempt for that id until the next reload.</summary>
    internal static bool XmlPortRealMetadataFailedForTests(int xmlPortId)
    {
        lock (_realXmlPortMetadataLock) return _xmlPortsRealMetadataFailed.Contains(xmlPortId);
    }

    /// <summary>
    /// Clear the "already loaded" / "already failed" bookkeeping alongside
    /// <c>_metaXmlPortCache</c> on a <c>--watch</c> / <c>--server</c> reload (#3172). The
    /// verbatim mirror of <see cref="ResetPageMetadataForReload"/>, which this file's header
    /// already declares itself a direct sibling of.
    /// <para>
    /// Both sets name ONE specific <c>NCLMetaXmlPort</c> instance — "this object's
    /// <c>metadataLoaded</c> flag has been cleared and <c>LoadMetadata()</c> has run on it",
    /// or "was attempted and threw". <see cref="ResetForReload"/> discards exactly those
    /// instances via <c>_metaXmlPortCache.Clear()</c>, so leaving either set populated makes
    /// <see cref="EnsureRealXmlPortMetadata"/> answer questions about a generation of objects
    /// that no longer exists.
    /// </para>
    /// <para>
    /// The success set surviving is the live defect: the next lookup rebuilds a brand-new,
    /// never-loaded skeleton through <c>_metaXmlPortCache.GetOrAdd(id, BuildNCLMetaXmlPort)</c>
    /// and then short-circuits it as "already loaded" — and per this file's header, an
    /// NCLMetaXmlPort with <c>metadataLoaded = true</c> and no schema is exactly what makes
    /// every real xmlport operation NRE (<c>CreateObjectInstance</c> has no node tree to
    /// instantiate, <c>GetMetadataFromLoader</c> has nothing to read).
    /// </para>
    /// <para>
    /// The failure set is cleared for the mirror reason, not merely for symmetry: an xmlport
    /// whose load failed against the previous generation must get a fresh attempt against
    /// this one, or the edit that fixes the cause could never be observed to have fixed it.
    /// One retry per cycle, not a retry storm, and every failed attempt still logs loudly
    /// from <see cref="EnsureRealXmlPortMetadata"/>'s catch block.
    /// </para>
    /// </summary>
    internal static void ResetXmlPortMetadataForReload()
    {
        lock (_realXmlPortMetadataLock)
        {
            _xmlPortsWithRealMetadata.Clear();
            _xmlPortsRealMetadataFailed.Clear();
        }
    }

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

                BcShape.Method(
                    meta.GetType(), "LoadMetadata",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    "XmlPort Metadata read from BC's own xmlport metadata")
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
