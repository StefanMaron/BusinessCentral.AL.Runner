// AlXmlPortMetadataRegistry — the per-xmlport runtime metadata XML that BC's
// Compilation.Emit delivers to the ModuleOutputter, exactly as for reports
// (AlReportMetadataRegistry) and pages (AlPageMetadataRegistry).
//
// WHY THIS EXISTS
//   The runner built NCLMetaXmlPort via CreateEmptyNCLMetaXmlPort(loader: null, …) and
//   force-set metadataLoaded = true, which is enough for "the metadata lookup must find
//   an entry" and nothing more. Every real xmlport operation then died:
//   NCLMetaXmlPort.CreateObjectInstance and NCLMetaApplicationObject.GetMetadataFromLoader
//   both NRE with no schema to read.
//
//   The emit hands us the full schema — 5-8 KB of XML per corpus xmlport — so BC's own
//   NCLMetaXmlPort.LoadMetadata() can build a real MetaXmlPort and BC's own XmlPort engine
//   does the import/export. That is the whole point of the runner: run MS's code, don't
//   reimplement it.
//
// CACHE-HIT SAFETY
//   Emit runs only on a compile-cache MISS, so anything captured here and not persisted is
//   silently gone on the next warm run — the registry is simply empty and every consumer
//   takes its not-found branch. Persisted by both cache layers exactly like the page
//   registry: the bundle sidecar in Program.cs (schema v8) and the dependency sidecar in
//   DependencyLoader.cs.
//
// Any suite that exercises this MUST be run twice — once cold (MISS) and once warm (HIT).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlRunner;

public static class AlXmlPortMetadataRegistry
{
    private static readonly ConcurrentDictionary<int, string> _xmlById = new();

    public static void Register(int xmlPortId, string metadataXml)
    {
        if (xmlPortId <= 0 || string.IsNullOrEmpty(metadataXml)) return;
        _xmlById[xmlPortId] = metadataXml;
        var trace = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_XMLPORT_METADATA");
        if (trace == "1" || trace == "2")
            Console.Out.WriteLine($"[xmlport-metadata] registered xmlport {xmlPortId} ({metadataXml.Length} chars)");
        if (trace == "2")
            Console.Out.WriteLine($"[xmlport-metadata] xmlport {xmlPortId} XML:\n{metadataXml}");
    }

    public static bool TryGet(int xmlPortId, out string metadataXml)
        => _xmlById.TryGetValue(xmlPortId, out metadataXml!);

    public static int Count => _xmlById.Count;

    public static void Clear() => _xmlById.Clear();

    /// <summary>Snapshot of the xmlport ids currently registered (diagnostics + dep sidecars).</summary>
    public static int[] Ids => _xmlById.Keys.ToArray();

    /// <summary>
    /// Serialize only the given xmlport ids — the dependency compile cache must not leak
    /// sibling-app entries into its own sidecar. Returns the entry count written.
    /// </summary>
    public static int SaveSidecar(string path, IEnumerable<int> onlyIds)
    {
        var idSet = new HashSet<int>(onlyIds);
        var dto = new
        {
            xmlPorts = _xmlById.Where(kv => idSet.Contains(kv.Key))
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
        if (!doc.RootElement.TryGetProperty("xmlPorts", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("xmlport-metadata.json: missing 'xmlPorts' array");
        int count = 0;
        foreach (var e in arr.EnumerateArray())
        {
            Register(e.GetProperty("id").GetInt32(), e.GetProperty("xml").GetString() ?? string.Empty);
            count++;
        }
        return count;
    }
}
