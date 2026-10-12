// SiblingSurface — what an in-bundle sibling dependency looked like to the apps compiled against
// it, so an app whose own files did not change can still tell that its dependency's did (#4125).
//
// A sibling app has no .app in any package cache. The dependent compiles against the
// *.symbols.json (and deps sidecar) EmitSiblingSymbols writes for it, which makes those two
// files the whole of what the dependent reads from the sibling. Hashing them is therefore an
// exact "did the surface this app was compiled against move" test, with no model of what a
// surface change is. Two reuse decisions were blind to it: the AL-output cache key (own .al
// bytes and package dependencies only) and the --watch incremental path (own .al files only).
using System.Security.Cryptography;
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>
/// The fingerprints of the previous pass, kept for the life of a run (the --watch loop owns one),
/// so a pass can say which sibling surfaces moved since. The first pass has nothing to compare
/// against and reports no change: nothing was compiled against an earlier surface yet.
/// </summary>
internal sealed class SiblingSurfaceMemory
{
    // (suite dir, app id): two suites in one tree can share an app id (EmitSiblingSymbols).
    private readonly Dictionary<(string SuiteDir, Guid AppId), string> _last = new();

    /// <summary>Stores <paramref name="fingerprint"/> and says whether it differs from the stored
    /// one. False when nothing was stored.</summary>
    internal bool Record(string suiteDir, Guid appId, string fingerprint)
    {
        var key = (Path.TrimEndingDirectorySeparator(suiteDir), appId);
        var moved = _last.TryGetValue(key, out var before) && !string.Equals(before, fingerprint, StringComparison.Ordinal);
        _last[key] = fingerprint;
        return moved;
    }
}

/// <summary>
/// The surfaces one <c>EmitSiblingSymbols</c> pass produced and which of them moved.
/// </summary>
internal sealed class SiblingSurfaces
{
    /// <summary>No sibling dependencies: every query answers "nothing".</summary>
    internal static readonly SiblingSurfaces None = new(
        new Dictionary<Guid, string>(), new HashSet<Guid>(), new Dictionary<Guid, AppGroup>());

    /// <summary>What a fingerprint reads as when the symbols could not be built or read. It is a
    /// fingerprint like any other, so it differs from every real one.</summary>
    internal const string Unavailable = "unavailable";

    private readonly IReadOnlyDictionary<Guid, string> _fingerprints;
    private readonly IReadOnlySet<Guid> _moved;
    private readonly IReadOnlyDictionary<Guid, AppGroup> _groups;

    internal SiblingSurfaces(
        IReadOnlyDictionary<Guid, string> fingerprints, IReadOnlySet<Guid> moved,
        IReadOnlyDictionary<Guid, AppGroup> groups)
    {
        _fingerprints = fingerprints;
        _moved = moved;
        _groups = groups;
    }

    /// <summary>
    /// The name of the first sibling <paramref name="group"/> declares a dependency on whose
    /// surface moved this pass, or null. <c>moved</c> already carries the propagation: an app
    /// rebuilt because its dependency moved counts as moved for ITS dependents, because it may
    /// re-export that dependency's types.
    /// </summary>
    internal string? MovedDependencyOf(AppGroup group)
    {
        foreach (var dep in group.DependsOn)
            if (_moved.Contains(dep))
                return _groups.TryGetValue(dep, out var g) ? g.ModuleName : dep.ToString();
        return null;
    }

    /// <summary>
    /// One line per sibling <paramref name="group"/> compiles against, directly or through
    /// another sibling, for the AL-output cache key. Sorted, so the key does not depend on the
    /// order the siblings were compiled in. Empty for an app with no sibling dependency, which
    /// leaves its key exactly as it was.
    /// </summary>
    internal IReadOnlyList<string> CacheTerms(AppGroup group)
    {
        if (_fingerprints.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<Guid>(group.DependsOn);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!_groups.TryGetValue(id, out var g) || !seen.Add(id)) continue;
            foreach (var next in g.DependsOn) pending.Push(next);
        }
        return seen
            .Select(id => $"sibling:{id:N}:{(_fingerprints.TryGetValue(id, out var f) ? f : "none")}")
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// SHA-256 over the symbols file and the dependency sidecar beside it, or
    /// <see cref="Unavailable"/> when the symbols file is missing or unreadable. A missing
    /// sidecar is not an error: it hashes as empty.
    ///
    /// <para>The symbols are hashed as a value, not as bytes: the incremental writer emits the
    /// empty lists (<c>"Tables":[]</c>) the whole-module writer leaves out, so one unchanged
    /// surface has two spellings depending on which path wrote it last. Empty and null properties
    /// are dropped and property order is fixed; ARRAY order is kept, because a parameter list is
    /// ordered.</para>
    /// </summary>
    internal static string Fingerprint(string symbolsFile, string sidecarFile)
    {
        try
        {
            if (!File.Exists(symbolsFile)) return Unavailable;
            using var sha = SHA256.Create();
            Absorb(sha, Canonical(File.ReadAllBytes(symbolsFile)));
            Absorb(sha, File.Exists(sidecarFile) ? File.ReadAllBytes(sidecarFile) : Array.Empty<byte>());
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (IOException) { return Unavailable; }
        catch (UnauthorizedAccessException) { return Unavailable; }
    }

    private static void Absorb(SHA256 sha, byte[] bytes)
    {
        sha.TransformBlock(BitConverter.GetBytes(bytes.Length), 0, 4, null, 0);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    /// <summary>The value form of <paramref name="json"/>; the bytes themselves when it is not JSON,
    /// which can only over-report a change.</summary>
    private static byte[] Canonical(byte[] json)
    {
        try
        {
            var start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
            using var doc = JsonDocument.Parse(json.AsMemory(start));
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms)) WriteCanonical(doc.RootElement, w);
            return ms.ToArray();
        }
        catch (JsonException) { return json; }
    }

    private static void WriteCanonical(JsonElement e, Utf8JsonWriter w)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().Where(p => !IsAbsent(p.Value)).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteCanonical(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) WriteCanonical(item, w);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    private static bool IsAbsent(JsonElement v) =>
        v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);
}
