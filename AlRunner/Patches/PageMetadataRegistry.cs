// AlPageMetadataRegistry — the per-page runtime metadata XML that BC's
// Compilation.Emit delivers to the ModuleOutputter: the same XML the service tier
// stores in Application Object Metadata at publish time, and that
// NCLMetaForm.LoadMetadata() parses into a real MetaForm with its full control tree.
//
// WHY THIS EXISTS
//   The runner builds NCLMetaForm via CreateEmptyNCLMetaForm(loader: null, …) and then
//   force-sets metadataLoaded = true, so the page's control tree never exists. That is
//   fine for "the metadata lookup must find an entry", which is all it was built for,
//   but it is the reason TestPage is a record cursor rather than a page: without a
//   control tree BC's own NavForm cannot register its source expressions, so a control
//   bound to anything other than a Rec field has nowhere to resolve to.
//
//   Exact same root cause, and exact same fix, as the report side one layer up —
//   see RunnerXmlMetadataLoader.cs, whose header already names page metadata as the
//   remaining gap.
//
// CACHE-HIT SAFETY (the trap this whole class exists to avoid)
//   Emit runs only on a compile-cache MISS. Anything captured solely at emit is GONE on
//   the next warm run, and the failure is silent — the registry is simply empty and every
//   consumer takes its not-found branch. That is precisely how AL queries broke on a warm
//   cache (fixed by the .query-symbols.json sidecar). So this registry is persisted by
//   both cache layers, exactly like the report registry: the bundle sidecar in Program.cs
//   and the dependency sidecar in DependencyLoader.cs.
//
// Any suite that exercises this MUST be run twice — once cold (MISS) and once warm (HIT).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlRunnerV2;

public static class AlPageMetadataRegistry
{
    private static readonly ConcurrentDictionary<int, string> _xmlById = new();

    public static void Register(int pageId, string metadataXml)
    {
        if (pageId <= 0 || string.IsNullOrEmpty(metadataXml)) return;
        _xmlById[pageId] = metadataXml;
        if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
            Console.Out.WriteLine($"[page-metadata] registered page {pageId} ({metadataXml.Length} chars of metadata XML)");
    }

    public static bool TryGet(int pageId, out string metadataXml)
        => _xmlById.TryGetValue(pageId, out metadataXml!);

    public static int Count => _xmlById.Count;

    public static void Clear() => _xmlById.Clear();

    /// <summary>Snapshot of the page ids currently registered (diagnostics + dep sidecars).</summary>
    public static int[] Ids => _xmlById.Keys.ToArray();

    /// <summary>
    /// Serialize only the given page ids — the dependency compile cache must not leak
    /// sibling-app entries into its own sidecar. Returns the entry count written.
    /// </summary>
    public static int SaveSidecar(string path, IEnumerable<int> onlyIds)
    {
        var idSet = new HashSet<int>(onlyIds);
        var dto = new
        {
            pages = _xmlById.Where(kv => idSet.Contains(kv.Key))
                            .Select(kv => new { id = kv.Key, xml = kv.Value })
                            .OrderBy(e => e.id)
                            .ToArray()
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dto));
        return idSet.Count;
    }

    /// <summary>
    /// Replay entries from a sidecar file. Throws on corrupt JSON — callers treat that
    /// as a cache MISS. Returns the replayed entry count.
    /// </summary>
    public static int LoadSidecar(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("pages", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("page-metadata.json: missing 'pages' array");
        int count = 0;
        foreach (var e in arr.EnumerateArray())
        {
            Register(e.GetProperty("id").GetInt32(), e.GetProperty("xml").GetString() ?? string.Empty);
            count++;
        }
        return count;
    }
}
