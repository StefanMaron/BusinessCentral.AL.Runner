// SiblingSurface — what an in-bundle sibling dependency looked like to the apps compiled against
// it, so an app whose own files did not change can still tell that its dependency's did (#4125).
//
// A sibling app has no .app in any package cache. The dependent compiles against the
// *.symbols.json (and deps sidecar) EmitSiblingSymbols writes for it, so hashing those two files
// says which surface a compile saw. Two reuse decisions were blind to it: the AL-output cache key
// (own .al bytes and package dependencies only) and the --watch incremental path (own .al files
// only). The incremental path is safe only while its baseline was compiled against the surface
// the dependencies have NOW, which is what SiblingSurfaceMemory records and compares.
using System.Security.Cryptography;
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>One thing an app's compile depends on that its own files do not show: the term that
/// identifies its current state, and a name for a message.</summary>
internal readonly record struct DependencyTerm(string Term, string Name);

/// <summary>
/// For each --watch app, the dependency terms its last SUCCESSFUL whole-module compile was built
/// against. The incremental path replays the app's unchanged files from that compile, so it may run
/// only while the terms are still the current ones. A whole-module compile forgets the entry when it
/// starts and records it only on success, so a failed one (AL0132 in two files, one then fixed) and
/// an AL-output cache HIT (which compiles nothing) both leave the app without a replayable baseline
/// until a whole-module compile succeeds against the current surface.
/// </summary>
internal sealed class SiblingSurfaceMemory
{
    private readonly Dictionary<string, IReadOnlyList<string>> _builtAgainst = new();

    private static string Key(string suiteDir) => Path.TrimEndingDirectorySeparator(suiteDir);

    internal void Forget(string suiteDir) => _builtAgainst.Remove(Key(suiteDir));

    internal void Built(string suiteDir, IReadOnlyList<DependencyTerm> terms) =>
        _builtAgainst[Key(suiteDir)] = terms.Select(t => t.Term).ToList();

    /// <summary>
    /// Null when the baseline of <paramref name="suiteDir"/> may be replayed; otherwise the reason,
    /// naming the first dependency whose term differs. An app with no such dependency is always
    /// replayable: nothing here concerns it.
    /// </summary>
    internal string? WhyNotReplayable(string suiteDir, IReadOnlyList<DependencyTerm> current)
    {
        if (current.Count == 0) return null;
        if (!_builtAgainst.TryGetValue(Key(suiteDir), out var built))
            return "no whole-module compile of it has succeeded against its dependencies' current state";
        var known = built.ToHashSet(StringComparer.Ordinal);
        var changed = current.FirstOrDefault(t => !known.Contains(t.Term));
        if (changed.Term == null && built.Count == current.Count) return null;
        return changed.Term == null
            ? "its set of dependencies changed since the compile its baseline was built from"
            : $"'{changed.Name}' changed since the compile its baseline was built from";
    }
}

/// <summary>
/// The surfaces one <c>EmitSiblingSymbols</c> pass produced.
/// </summary>
internal sealed class SiblingSurfaces
{
    /// <summary>No sibling dependencies: every query answers "nothing".</summary>
    internal static readonly SiblingSurfaces None = new(
        new Dictionary<Guid, string>(), new Dictionary<Guid, AppGroup>());

    /// <summary>What a fingerprint reads as when the symbols could not be built or read. It is a
    /// fingerprint like any other, so it differs from every real one.</summary>
    internal const string Unavailable = "unavailable";

    private readonly IReadOnlyDictionary<Guid, string> _fingerprints;
    private readonly IReadOnlyDictionary<Guid, AppGroup> _groups;

    internal SiblingSurfaces(IReadOnlyDictionary<Guid, string> fingerprints, IReadOnlyDictionary<Guid, AppGroup> groups)
    {
        _fingerprints = fingerprints;
        _groups = groups;
    }

    /// <summary>
    /// One term per sibling <paramref name="group"/> compiles against, directly or through another
    /// sibling (a chain moves whichever link changed). Sorted, so the order the siblings were
    /// compiled in does not matter. Empty for an app with no sibling dependency.
    /// </summary>
    internal IReadOnlyList<DependencyTerm> Terms(AppGroup group)
    {
        if (_fingerprints.Count == 0) return Array.Empty<DependencyTerm>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<Guid>(group.DependsOn);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!_groups.TryGetValue(id, out var g) || !seen.Add(id)) continue;
            foreach (var next in g.DependsOn) pending.Push(next);
        }
        return seen
            .Select(id => new DependencyTerm(
                $"sibling:{id:N}:{(_fingerprints.TryGetValue(id, out var f) ? f : "none")}", _groups[id].ModuleName))
            .OrderBy(t => t.Term, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The terms as lines for the AL-output cache key.</summary>
    internal IReadOnlyList<string> CacheTerms(AppGroup group) => Terms(group).Select(t => t.Term).ToList();

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
