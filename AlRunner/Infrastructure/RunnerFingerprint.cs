// RunnerFingerprint — the runner-identity component shared by every on-disk cache key
// (AL-output cache, source-workspace cache, source-dependency cache).
//
// Issue #1815, findings 2+3:
//
//   Finding 2 — every cache key used to write
//       runner:{File.GetLastWriteTimeUtc(runnerLoc).Ticks}:{length}
//   Every CI leg rebuilds the runner before running tests, so mtime moves on every run
//   even when the assembly's bytes are byte-for-byte identical. A cache persisted across
//   CI runs (actions/cache) would therefore MISS 100% of the time — the mtime line alone
//   defeated the entire point of persisting the cache.
//
//   Finding 3 — the trap in fixing finding 2 by itself. Replace the mtime with a content
//   hash and the runner's *content* is identical across all 8 BC-version legs (same
//   commit, same build) — so without something else in the key, all 8 legs would collide
//   on ONE cache entry, and whichever leg wrote first would poison every other leg with AL
//   output compiled against a different BC version's symbols. Today that collision is
//   avoided only *by accident*, because the mtime differs per leg's independent rebuild.
//
// Fix: hash the runner assembly's CONTENT (stable across rebuilds of unchanged source,
// unlike mtime) and pair it with an EXPLICIT bc:<version> line (so content-identical
// runners across legs still produce distinct keys per BC version). Both must land
// together — shipping finding 2 without finding 3 is a cache-poisoning regression, not
// a fix.
//
// SHA-256 of the full assembly bytes was chosen over the module's MVID. A clean Release
// rebuild of an unchanged source tree was verified (see issue #1815) to produce
// byte-identical output (.NET SDK's Deterministic=true default is on for this project),
// so the MVID would be equally stable here as a fingerprint — but hashing the ~1.6 MB
// al-runner.dll costs low single-digit milliseconds (measured: ~4 ms), immaterial next to
// the seconds an AL emit+compile takes, and a plain SHA-256 over the file avoids depending
// on PE/metadata-reader plumbing to extract an MVID from whatever on-disk shape a future
// publish (single-file, trimmed, ReadyToRun) leaves the assembly in. Simpler wins when the
// cost difference doesn't matter.
namespace AlRunner.Infrastructure;

public static class RunnerFingerprint
{
    private static string? _cachedContentHash;
    private static readonly object _lock = new();

    /// <summary>
    /// Hex-encoded SHA-256 of the running al-runner assembly's bytes ("unknown" if the
    /// assembly has no on-disk location, e.g. hosted in-memory in a test harness).
    /// Computed once per process and cached: every cache-key computation in a run
    /// (one per compiled bundle/dependency) reuses the same hash rather than re-reading
    /// and re-hashing the ~1.6 MB file each time.
    /// </summary>
    public static string ContentHash
    {
        get
        {
            if (_cachedContentHash != null) return _cachedContentHash;
            lock (_lock)
            {
                if (_cachedContentHash != null) return _cachedContentHash;
                var loc = typeof(RunnerFingerprint).Assembly.Location;
                _cachedContentHash = ComputeContentHash(loc);
                return _cachedContentHash;
            }
        }
    }

    /// <summary>
    /// Core hash computation, factored out so tests can point it at arbitrary bytes on
    /// disk without needing to swap out the running assembly. Returns "unknown" for a
    /// missing/empty path, mirroring the previous mtime-based line's "runner:unknown"
    /// fallback for a location-less assembly.
    /// </summary>
    public static string ComputeContentHash(string assemblyLocation)
    {
        if (string.IsNullOrEmpty(assemblyLocation) || !File.Exists(assemblyLocation))
            return "unknown";
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(assemblyLocation);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Writes the two cache-key framing lines every cache key must carry: the runner
    /// content fingerprint (finding 2) and the selected BC version (finding 3). Kept
    /// together in one helper so no future cache key can add one without the other.
    /// This overload uses the running process's own fingerprint and its resolved
    /// <see cref="BcArtifacts.SelectedVersion"/> — the production call sites.
    /// Callers must have already resolved the BC version (true for every existing call
    /// site — version selection happens once at startup, before any bundle/dependency
    /// compile) since reading it here for the first time would trigger BcArtifacts' lazy
    /// latest-in-cache default, silently picking a version rather than honouring the
    /// run's actual selection.
    /// </summary>
    public static void WriteKeyLines(Action<string> writeLine)
        => WriteKeyLines(writeLine, ContentHash, BcArtifacts.SelectedVersion);

    /// <summary>
    /// Testable core of <see cref="WriteKeyLines(Action{string})"/>: takes the content
    /// hash and BC version explicitly so a test can vary either independently without
    /// needing to swap out the running assembly or the process-global BC version
    /// selection.
    /// </summary>
    public static void WriteKeyLines(Action<string> writeLine, string contentHash, Version bcVersion)
    {
        writeLine($"runner:{contentHash}");
        writeLine($"bc:{bcVersion}");
    }
}
