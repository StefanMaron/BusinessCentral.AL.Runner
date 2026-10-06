// AlCacheInputs — what an AL-output cache entry's compile read besides `.al` and app.json, and the
// check that a HIT is still the answer for those files (#5368).
//
// The cache key is computed before any compile, from the `.al` files and app.json. BC's compiler
// also reads a report's layout file, a ControlAddIn's resources and the Translations folder, so a
// change to only one of those left the key identical and the next run a HIT: a deleted layout the
// compile refuses (AL1081) kept passing. The population is not listed here: it is what the compile
// asked its IFileSystem for (CompileFileReads), recorded beside the entry and re-fingerprinted on a
// HIT. A mismatch is a MISS. Rules: docs/server-mode.md#the-al-output-cache-and-files-the-compile-reads.
//
// `<key>.inputs.json` is also the entry's COMMIT RECORD: it names the SHA-256 of the DLL and of the
// sidecars it was published with, and is published LAST. An entry is overwritten under the same key
// when an input changed, so a reader can meet the new DLL beside the old record, or the old DLL
// beside a new sidecar: either reads as a mismatch here, which is a MISS and never a HIT.
using System.Security.Cryptography;
using System.Text.Json;

namespace AlRunner.Infrastructure;

internal static class AlCacheInputs
{
    public const string Suffix = ".inputs.json";

    private const int FormatVersion = 1;

    internal enum State { Valid, Unreadable, ArtifactsDiffer, InputChanged }

    internal readonly record struct Verdict(State State, string Detail)
    {
        public bool IsValid => State == State.Valid;
    }

    private sealed class Record
    {
        public int Format { get; set; }
        public Dictionary<string, string> Artifacts { get; set; } = new();
        public Dictionary<string, string> Inputs { get; set; } = new();
    }

    internal static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    /// <summary>
    /// Writes the record to <paramref name="path"/> (the temp file AlCacheWriter.AtomicPublish hands
    /// out). <paramref name="fingerprints"/> is what <c>BcEmitOutput.CompileInputs</c> holds, keyed
    /// by absolute path; null when the compile had no app root to read from.
    /// </summary>
    internal static void Write(
        string path, string? appRootDir, IReadOnlyDictionary<string, string>? fingerprints,
        IReadOnlyDictionary<string, string> artifactHashes)
    {
        var record = new Record { Format = FormatVersion };
        foreach (var kv in artifactHashes) record.Artifacts[kv.Key] = kv.Value;
        if (fingerprints != null && appRootDir != null)
            foreach (var kv in fingerprints) record.Inputs[ToPortable(kv.Key, appRootDir)] = kv.Value;
        File.WriteAllText(path, JsonSerializer.Serialize(record));
    }

    /// <summary>
    /// <see cref="Verify"/> for the entry on disk: the DLL bytes the reader already holds, and the
    /// sidecars it is about to replay (the query sidecar only when the bundle declares a query).
    /// </summary>
    internal static Verdict VerifyEntry(
        string recordPath, string? appRootDir, byte[] dllBytes, string enumSidecarPath, string? querySidecarPath)
    {
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".dll"] = HashBytes(dllBytes),
                [AlCacheSidecars.EnumRegistrySuffix] = HashFile(enumSidecarPath),
            };
            if (querySidecarPath != null) hashes[AlCacheSidecars.QuerySymbolsSuffix] = HashFile(querySidecarPath);
            return Verify(recordPath, appRootDir, hashes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Verdict(State.Unreadable, ex.Message);
        }
    }

    /// <summary>The hashes to record for what was just published; the query sidecar only when one was written.</summary>
    internal static Dictionary<string, string> Hashes(byte[] dllBytes, string enumSidecarHash, string? querySidecarHash)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".dll"] = HashBytes(dllBytes),
            [AlCacheSidecars.EnumRegistrySuffix] = enumSidecarHash,
        };
        if (querySidecarHash != null) hashes[AlCacheSidecars.QuerySymbolsSuffix] = querySidecarHash;
        return hashes;
    }

    /// <summary>
    /// Whether the entry whose artifacts hash to <paramref name="artifactHashes"/> still answers for
    /// the files under <paramref name="appRootDir"/>. Every outcome but Valid is a MISS.
    /// </summary>
    internal static Verdict Verify(
        string recordPath, string? appRootDir, IReadOnlyDictionary<string, string> artifactHashes)
    {
        Record? record;
        try { record = JsonSerializer.Deserialize<Record>(File.ReadAllText(recordPath)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Verdict(State.Unreadable, $"{Path.GetFileName(recordPath)}: {ex.Message}");
        }
        if (record == null || record.Format != FormatVersion || record.Artifacts == null || record.Inputs == null)
            return new Verdict(State.Unreadable, $"{Path.GetFileName(recordPath)}: not a format-{FormatVersion} record");

        foreach (var kv in artifactHashes)
            if (!record.Artifacts.TryGetValue(kv.Key, out var published)
                || !string.Equals(published, kv.Value, StringComparison.Ordinal))
                return new Verdict(State.ArtifactsDiffer,
                    $"{kv.Key} is not the one the record was published with");

        if (record.Inputs.Count == 0) return new Verdict(State.Valid, "");
        if (appRootDir == null)
            return new Verdict(State.Unreadable, "the record names inputs and this run has no app root to resolve them against");

        Dictionary<string, string> now;
        var absolute = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var key in record.Inputs.Keys) absolute[key] = FromPortable(key, appRootDir);
            now = CompileFileReads.Fingerprint(appRootDir, absolute.Values);
        }
        catch (InvalidOperationException ex)
        {
            return new Verdict(State.Unreadable, ex.Message);
        }

        var changed = record.Inputs
            .Where(kv => !string.Equals(now[absolute[kv.Key]], kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return changed.Count == 0
            ? new Verdict(State.Valid, "")
            : new Verdict(State.InputChanged, string.Join(", ", changed));
    }

    // A path under the app root is stored relative to it, so a cache that moves with its bundle (a
    // restored CI cache, a different checkout) keeps answering; one outside it stays absolute.
    private static string ToPortable(string key, string root)
    {
        foreach (var prefix in new[] { CompileFileReads.FilePrefix, CompileFileReads.DirPrefix })
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var path = key[prefix.Length..];
            var relative = Path.GetRelativePath(root, path);
            var inside = !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            return prefix + (inside ? relative : path).Replace('\\', '/');
        }
        return key;   // a listing is already relative to the app root
    }

    private static string FromPortable(string key, string root)
    {
        foreach (var prefix in new[] { CompileFileReads.FilePrefix, CompileFileReads.DirPrefix })
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var path = key[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            return prefix + (Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path)));
        }
        return key;
    }
}
