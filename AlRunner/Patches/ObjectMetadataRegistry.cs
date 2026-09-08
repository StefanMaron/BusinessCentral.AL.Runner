// AlObjectMetadataRegistry — BC's own metadata document for EVERY application object
// the emitter hands to CaptureOutputter, keyed by (object kind, object id).
//
// The three registries next to this one (report, page, xmlport) each keep one kind.
// Everything else BC emits was discarded and then rebuilt from SymbolReference.json,
// from AL source text, or from symbol properties. This registry keeps the emitter's
// own answer for all of it. It is additive: nothing reads it yet, and the three
// per-kind registries keep their consumers unchanged (issue #3548, step 2).
//
// KEYING
//   Every kind that reaches AddApplicationObject with a non-empty metadata document is
//   id-bearing, and ids repeat across kinds — table 70660 and page 70660 are different
//   objects with different documents. So the key is (kind, id), never the id alone.
//   `id` is nullable anyway: an id-less kind (profile, controladdin, …) does not reach
//   this outputter today, and if one ever does it is keyed by name rather than dropped.
//   See docs/object-metadata-capture.md for which kinds arrive and which do not.
//
// CACHE-HIT SAFETY
//   Emit runs only on a compile-cache MISS, so anything captured here and not persisted
//   is gone on the next warm run — silently, with every consumer taking its not-found
//   branch. This registry is replayed by the same three mechanisms the page registry
//   uses: the bundle's `.enum-registry.json` sidecar (ProgramSupport.Save/Load
//   EnumRegistrySidecar), the dependency compile cache's `.object-metadata.json`
//   sidecar (DependencyLoader), and the --watch RAD shadow snapshot
//   (BcCompiler.Incremental.cs). Any suite exercising it must be run twice.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlRunner;

/// <summary>One emitted object's metadata document, as BC produced it.</summary>
/// <param name="Kind">The AL compiler's own <c>SymbolKind</c> name — "Table", "Page",
/// "PermissionSetExtension", … Carried as a string so a kind BC adds later flows
/// through the capture, the sidecar and back without a code change.</param>
/// <param name="Id">The object's AL id, or null for a kind that has none.</param>
public sealed record AlObjectMetadataEntry(string Kind, int? Id, string Name, string Xml);

public static class AlObjectMetadataRegistry
{
    private static readonly ConcurrentDictionary<string, AlObjectMetadataEntry> _byKey =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The identity a document is stored under. Mirrors BcCompiler.Incremental's
    /// IdentityKey: id-bearing kinds key by id, id-less kinds by name.
    /// </summary>
    public static string KeyFor(string kind, int? id, string name)
        => id.HasValue ? $"{kind}|id:{id.Value}" : $"{kind}|name:{name}";

    public static void Register(string kind, int? id, string name, string metadataXml)
    {
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(metadataXml)) return;
        // An id-less registration needs a name to be addressable at all; without either
        // there is no identity to store it under.
        if (!id.HasValue && string.IsNullOrEmpty(name)) return;
        var key = KeyFor(kind, id, name);
        _byKey[key] = new AlObjectMetadataEntry(kind, id, name ?? string.Empty, metadataXml);

        var trace = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_OBJECT_METADATA");
        if (trace == "1" || trace == "2")
            Console.Out.WriteLine(id.HasValue
                ? $"[object-metadata] registered {kind} {id.Value} '{name}' ({metadataXml.Length} chars)"
                : $"[object-metadata] registered {kind} name '{name}' ({metadataXml.Length} chars)");
        // "2" dumps the document itself — the only way to see what BC's emitter actually
        // says about a property, as opposed to what the runner's own derivation says.
        if (trace == "2")
            Console.Out.WriteLine($"[object-metadata] {key} XML:\n{metadataXml}");
    }

    public static bool TryGet(string kind, int id, out string metadataXml)
    {
        if (_byKey.TryGetValue(KeyFor(kind, id, string.Empty), out var e))
        {
            metadataXml = e.Xml;
            return true;
        }
        metadataXml = string.Empty;
        return false;
    }

    public static bool TryGetByName(string kind, string name, out string metadataXml)
    {
        if (_byKey.TryGetValue(KeyFor(kind, null, name), out var e))
        {
            metadataXml = e.Xml;
            return true;
        }
        metadataXml = string.Empty;
        return false;
    }

    public static int Count => _byKey.Count;

    public static void Clear() => _byKey.Clear();

    /// <summary>Identity keys currently held — the unit both sidecar scoping and the
    /// --watch shadow snapshot diff on.</summary>
    public static string[] Keys => _byKey.Keys.ToArray();

    public static bool TryGetByKey(string key, out AlObjectMetadataEntry entry)
        => _byKey.TryGetValue(key, out entry!);

    /// <summary>Every entry, ordered so two snapshots of the same state serialize
    /// byte-identically.</summary>
    public static IReadOnlyList<AlObjectMetadataEntry> Snapshot()
        => _byKey.Values
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Id ?? int.MinValue)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Serialize only the given identity keys — a dependency's own sidecar must not
    /// carry a sibling app's entries. Returns the number written.
    /// </summary>
    public static int SaveSidecar(string path, IEnumerable<string> onlyKeys)
    {
        var wanted = new HashSet<string>(onlyKeys, StringComparer.Ordinal);
        var entries = Snapshot()
            .Where(e => wanted.Contains(KeyFor(e.Kind, e.Id, e.Name)))
            .Select(e => new { kind = e.Kind, id = e.Id, name = e.Name, xml = e.Xml })
            .ToArray();
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { objects = entries }));
        return entries.Length;
    }

    /// <summary>
    /// Replay entries from a sidecar file. Throws on corrupt JSON — callers treat that
    /// as a cache MISS. Returns the replayed entry count.
    /// </summary>
    public static int LoadSidecar(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("objects", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("object-metadata.json: missing 'objects' array");
        return LoadFromJsonArray(arr);
    }

    /// <summary>Replay an <c>objects</c> array, wherever it is embedded — the bundle's
    /// AL-output cache folds it into the enum-registry sidecar rather than writing a
    /// second file.</summary>
    public static int LoadFromJsonArray(System.Text.Json.JsonElement arr)
    {
        int count = 0;
        foreach (var e in arr.EnumerateArray())
        {
            int? id = e.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? idEl.GetInt32()
                    : null;
            Register(
                e.GetProperty("kind").GetString() ?? string.Empty,
                id,
                e.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                e.GetProperty("xml").GetString() ?? string.Empty);
            count++;
        }
        return count;
    }
}
