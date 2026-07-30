// AlReportMetadataRegistry — carries the per-report runtime metadata XML that
// BC's Compilation.Emit delivers to the ModuleOutputter (the same XML the
// service tier stores in Application Object Metadata at publish time and that
// NCLMetaReport/MetaReport parse at run time).
//
// Populated from two producers:
//   1. BcCompiler.CaptureOutputter.AddApplicationObject — test-bundle emits.
//   2. DependencyLoader source-dep compiles (same outputter path).
// Both cache layers persist a `.report-metadata.json` sidecar so cache HITs
// replay the registry exactly like the enum-registry sidecar does.
//
// Consumed by NavReportSync.StubInitializeMetadata, which constructs a REAL
// Microsoft.Dynamics.Nav.Types.Metadata.MetaReport from this XML so the BC
// report execution chain (RunReportInternalCoreAsync → ExecuteDataItemIterator
// → NavDataSetBuilder) runs on genuine metadata instead of a skeleton stub.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlRunner;

public static class AlReportMetadataRegistry
{
    private static readonly ConcurrentDictionary<int, string> _xmlById = new();

    public static void Register(int reportId, string metadataXml)
    {
        if (reportId <= 0 || string.IsNullOrEmpty(metadataXml)) return;
        _xmlById[reportId] = metadataXml;
    }

    public static bool TryGet(int reportId, out string metadataXml)
        => _xmlById.TryGetValue(reportId, out metadataXml!);

    public static int Count => _xmlById.Count;

    public static void Clear() => _xmlById.Clear();

    /// <summary>Serialize the registry to a sidecar file. Returns entry count.</summary>
    public static int SaveSidecar(string path)
    {
        var dto = new
        {
            reports = _xmlById.Select(kv => new { id = kv.Key, xml = kv.Value })
                              .OrderBy(e => e.id)
                              .ToArray()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        File.WriteAllText(path, json);
        return _xmlById.Count;
    }

    /// <summary>
    /// Serialize only the given report ids (used by the dependency compile cache,
    /// which must not leak sibling-app entries into its own sidecar).
    /// </summary>
    public static int SaveSidecar(string path, IEnumerable<int> onlyIds)
    {
        var idSet = new HashSet<int>(onlyIds);
        var dto = new
        {
            reports = _xmlById.Where(kv => idSet.Contains(kv.Key))
                              .Select(kv => new { id = kv.Key, xml = kv.Value })
                              .OrderBy(e => e.id)
                              .ToArray()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        File.WriteAllText(path, json);
        return idSet.Count;
    }

    /// <summary>
    /// Replay entries from a sidecar file. Throws on corrupt JSON — callers
    /// treat that as cache MISS. Returns replayed entry count.
    /// </summary>
    public static int LoadSidecar(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("reports", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("report-metadata.json: missing 'reports' array");
        int count = 0;
        foreach (var e in arr.EnumerateArray())
        {
            int id = e.GetProperty("id").GetInt32();
            string xml = e.GetProperty("xml").GetString() ?? string.Empty;
            Register(id, xml);
            count++;
        }
        return count;
    }

    /// <summary>Snapshot of the report ids currently registered (diagnostics + dep sidecars).</summary>
    public static int[] Ids => _xmlById.Keys.ToArray();
}
