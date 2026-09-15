// RecordPatches.KnownQueryIds — every query id the runner knows exists.
//
// Deliberately the same three-source shape as KnownXmlPortIdSet (RecordPatches.
// KnownXmlPortIds.cs, #3510) and KnownReportIdSet (RecordPatches.ReportMetadataVirtualTable.cs),
// both of which were widened for the identical gap: an object living in a precompiled
// dependency is knowable from that dependency's SymbolReference.json and from its compiled
// type, never from AL source the runner parsed.
//
// Queries were the kind that never got this. RecordPatches.NclMetadataCachePopulator.cs
// populates reports from KnownReportIds() and xmlports from KnownXmlPortIdSet(), but queries
// from `_parsedQueries.Keys` alone — the source-parsed set. Serving the Query Metadata virtual
// table (2000000142) from that narrow set would report rows for the app under test and omit
// every Base Application query, which is precisely the disagreement KnownXmlPortIdSet's own
// comment exists to prevent: an object AL can see in AllObj and one this set answers for must
// not disagree (#4147).
using System.Collections.Concurrent;
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static HashSet<int>? _knownQueryIds;
    private static (int Epoch, int Parsed, int Assemblies) _knownQueryIdsBuiltFrom;

    /// <summary>
    /// Every query id the runner knows exists: source-parsed, declared by a registered
    /// precompiled dependency, or present as a compiled <c>Query{id}</c> type.
    ///
    /// <para>Existence only. Nothing here derives a query's elements or columns; the row the
    /// Query Metadata table reports is built by BC's own provider from the NCLMetaQuery the
    /// metadata cache resolves, and an id this set offers that has no resolvable metadata is
    /// skipped by BC's own <c>catch (NavMetadataNotFoundException)</c> rather than being
    /// fatal — which is what makes widening the set safe.</para>
    /// </summary>
    internal static HashSet<int> KnownQueryIdSet()
    {
        var generation = (BcAppRegistrationEpoch, _parsedQueries.Count,
            AppDomain.CurrentDomain.GetAssemblies().Length);
        if (_knownQueryIds != null && _knownQueryIdsBuiltFrom == generation) return _knownQueryIds;

        var ids = new HashSet<int>(_parsedQueries.Keys);
        foreach (var id in BcAppQueryIds())
            ids.Add(id);
        foreach (var id in CompiledQueryIds())
            ids.Add(id);
        _knownQueryIds = ids;
        _knownQueryIdsBuiltFrom = generation;
        return ids;
    }

    /// <summary>
    /// Query ids declared by a registered dependency .app, read from the same
    /// <see cref="BcAppSymbolCache.ObjectSymbol"/> set AllObj (2000000038) reports from — so a
    /// query AL can see in AllObj and one this set answers for cannot disagree.
    /// </summary>
    private static IEnumerable<int> BcAppQueryIds()
    {
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("queries (metadata skeleton)"))
            foreach (var o in symbols.Objects)
                if (NormalizeObjectTypeName(o.Kind) == "query")
                    yield return o.Id;
    }

    /// <summary>Per-assembly memo, mirroring <c>_compiledXmlPortIdsByAssembly</c>.</summary>
    private static readonly ConcurrentDictionary<Assembly, int[]> _compiledQueryIdsByAssembly = new();

    /// <summary>
    /// Query ids that exist as a compiled <c>Query{id}</c> type in a loaded assembly — the
    /// source that answers for a TIER-1 precompiled dependency, which has neither AL source the
    /// runner compiled nor necessarily a readable SymbolReference.json. AL emits that type name
    /// for every query (CodeunitPatches.cs constructs <c>Query{id}</c> directly by it), so its
    /// presence is proof the query exists, which is all this set claims.
    ///
    /// <para>Metadata-first, like <c>CompiledXmlPortIds</c>: the TypeDef Name column answers it
    /// with no ReflectionTypeLoadException half-answer to guard against, so the result is
    /// complete and safe to cache. A dynamic assembly has no metadata to read and keeps the
    /// reflection path, which deliberately does not cache a partial answer.</para>
    /// </summary>
    private static IEnumerable<int> CompiledQueryIds()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (_compiledQueryIdsByAssembly.TryGetValue(asm, out var cached))
            {
                foreach (var id in cached) yield return id;
                continue;
            }

            var index = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm);
            if (index.IsMetadataBacked)
            {
                var mdIds = ExtractQueryIdsFromNames(index.TypeNamesWithPrefix("Query"));
                _compiledQueryIdsByAssembly[asm] = mdIds;
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
                partialIdsOnException = ExtractQueryIdsFromNames(
                    rtle.Types.Where(t => t is not null).Select(t => t!.Name));
                types = null;
            }
            catch
            {
                types = null;
            }

            if (types != null)
            {
                var ids = ExtractQueryIdsFromNames(types.Select(t => t.Name));
                _compiledQueryIdsByAssembly[asm] = ids;
                foreach (var id in ids) yield return id;
            }
            else if (partialIdsOnException != null)
            {
                foreach (var id in partialIdsOnException) yield return id;
            }
        }
    }

    private static int[] ExtractQueryIdsFromNames(IEnumerable<string> typeNames)
    {
        List<int>? ids = null;
        foreach (var name in typeNames)
        {
            if (!TryParseQueryId(name, out var id)) continue;
            (ids ??= new List<int>()).Add(id);
        }
        return ids?.ToArray() ?? Array.Empty<int>();
    }

    private static bool TryParseQueryId(ReadOnlySpan<char> typeName, out int id)
    {
        id = 0;
        if (!typeName.StartsWith("Query", StringComparison.Ordinal)) return false;
        return int.TryParse(typeName[5..], out id) && id > 0;
    }
}
