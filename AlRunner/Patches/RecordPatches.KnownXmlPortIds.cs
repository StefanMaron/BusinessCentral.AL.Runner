// RecordPatches.KnownXmlPortIds — the existence set BuildNCLMetaXmlPort checks before
// building an xmlport's skeleton NCLMetaXmlPort (#3510).
//
// Deliberately the same three-source shape as KnownReportIdSet (RecordPatches.
// ReportMetadataVirtualTable.cs), which was widened for the identical gap on reports: an
// object living in a precompiled dependency is knowable from that dependency's
// SymbolReference.json and from its compiled type, never from AL source the runner parsed.
// Keeping the two builders' existence checks the same shape is what stops them drifting
// apart again — see docs/virtual-tables-allobj.md#object-existence.

using System.Collections.Concurrent;
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static HashSet<int>? _knownXmlPortIds;

    // Same generation key as KnownReportIdSet, for the same reason: a Tier-1 precompiled
    // dependency's xmlports only become knowable once its DLL is loaded, which happens AFTER
    // this set is first asked for, so the assembly count has to be part of the key or the
    // empty answer is memoized for the whole run. The .app term is the registration EPOCH
    // rather than _bcAppPaths.Count (#2888) — the registered set can shrink, so a count
    // cannot tell a set that lost N entries and gained N different ones from the one it was
    // built against.
    private static (int Epoch, int Parsed, int Assemblies) _knownXmlPortIdsBuiltFrom = (-1, -1, -1);

    /// <summary>
    /// Ids of every xmlport the runner knows exists — source-parsed ones, plus every xmlport
    /// declared by a registered dependency .app, plus every xmlport present as a compiled
    /// <c>XmlPort{id}</c> type in a loaded assembly.
    ///
    /// <para>Existence only. Nothing here derives an xmlport's node schema, and the skeleton
    /// builder does not need one: BuildNCLMetaXmlPort never reads the parsed value it used to
    /// look up (#3510). Real structure, when the runner has it, comes from
    /// AlXmlPortMetadataRegistry through EnsureRealXmlPortMetadata, which is tried first at
    /// the dispatch site and is unaffected by this set. What the symbol file does and does not
    /// state about an xmlport is #3797.</para>
    /// </summary>
    internal static HashSet<int> KnownXmlPortIdSet()
    {
        var generation = (BcAppRegistrationEpoch, _parsedXmlPorts.Count,
            AppDomain.CurrentDomain.GetAssemblies().Length);
        if (_knownXmlPortIds != null && _knownXmlPortIdsBuiltFrom == generation) return _knownXmlPortIds;

        var ids = new HashSet<int>(_parsedXmlPorts.Keys);
        foreach (var id in BcAppXmlPortIds())
            ids.Add(id);
        foreach (var id in CompiledXmlPortIds())
            ids.Add(id);
        _knownXmlPortIds = ids;
        _knownXmlPortIdsBuiltFrom = generation;
        return ids;
    }

    /// <summary>
    /// Xmlport ids declared by a registered dependency .app, read from the same
    /// <see cref="BcAppSymbolCache.ObjectSymbol"/> set AllObj (2000000038) reports from — so an
    /// xmlport AL can see in AllObj and one this builder will build for cannot disagree.
    /// </summary>
    private static IEnumerable<int> BcAppXmlPortIds()
    {
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("xmlports (metadata skeleton)"))
            foreach (var o in symbols.Objects)
                if (NormalizeObjectTypeName(o.Kind) == "xmlport")
                    yield return o.Id;
    }

    /// <summary>Per-assembly memo, mirroring <c>_compiledReportIdsByAssembly</c>.</summary>
    private static readonly ConcurrentDictionary<Assembly, int[]> _compiledXmlPortIdsByAssembly = new();

    /// <summary>
    /// Xmlport ids that exist as a compiled <c>XmlPort{id}</c> type in a loaded assembly.
    ///
    /// <para>This is the source that answers for a TIER-1 precompiled dependency, which has
    /// neither AL source the runner compiled nor — necessarily — a readable
    /// SymbolReference.json: DependencyLoader loads its DLL directly and never extracts or
    /// compiles AL. The compiled type is proof the xmlport exists, which is exactly what this
    /// set answers, and #3510's own stack frame
    /// (<c>Microsoft.Dynamics.Nav.BusinessApplication.XmlPort1230..ctor</c>) is that proof
    /// arriving: the type was already being constructed when the metadata lookup failed.</para>
    ///
    /// <para>Metadata-first, like <c>CompiledReportIds</c>: "does a type called XmlPort{id}
    /// exist here" is answerable from the TypeDef table's Name column, with no
    /// ReflectionTypeLoadException half-answer to guard against, so the result is always
    /// complete and always safe to cache. Dynamic assemblies have no metadata to read and keep
    /// the reflection path, which deliberately does not cache a partial answer.</para>
    /// </summary>
    private static IEnumerable<int> CompiledXmlPortIds()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (_compiledXmlPortIdsByAssembly.TryGetValue(asm, out var cached))
            {
                foreach (var id in cached) yield return id;
                continue;
            }

            var index = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm);
            if (index.IsMetadataBacked)
            {
                var mdIds = ExtractXmlPortIdsFromNames(index.TypeNamesWithPrefix("XmlPort"));
                _compiledXmlPortIdsByAssembly[asm] = mdIds;
                foreach (var id in mdIds) yield return id;
                continue;
            }

            Type[]? types;
            int[]? partialIdsOnException = null;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // A partial load — use what DID load for this yield, but never cache it as the
                // permanent answer; the assembly may be more loadable on a later call.
                partialIdsOnException = ExtractXmlPortIdsFromNames(
                    rtle.Types.Where(t => t is not null).Select(t => t!.Name));
                types = null;
            }
            catch
            {
                types = null;
            }

            if (types is null)
            {
                if (partialIdsOnException is not null)
                    foreach (var id in partialIdsOnException) yield return id;
                continue;
            }

            var scanned = ExtractXmlPortIdsFromNames(types.Select(t => t.Name));
            _compiledXmlPortIdsByAssembly[asm] = scanned;
            foreach (var id in scanned) yield return id;
        }
    }

    private static int[] ExtractXmlPortIdsFromNames(IEnumerable<string> typeNames)
    {
        List<int>? ids = null;
        foreach (var name in typeNames)
        {
            if (!TryParseXmlPortId(name, out var id)) continue;
            (ids ??= new List<int>()).Add(id);
        }
        return ids?.ToArray() ?? Array.Empty<int>();
    }

    /// <summary>
    /// <c>XmlPort</c> followed by a positive integer and nothing else. A name that merely
    /// starts with "XmlPort" and has a non-numeric suffix yields nothing — which is what keeps
    /// BC's own <c>XmlPortManagement</c>-style helper types out of the set.
    /// </summary>
    private static bool TryParseXmlPortId(ReadOnlySpan<char> typeName, out int id)
    {
        id = 0;
        if (!typeName.StartsWith("XmlPort", StringComparison.Ordinal)) return false;
        return int.TryParse(typeName[7..], out id) && id > 0;
    }
}
