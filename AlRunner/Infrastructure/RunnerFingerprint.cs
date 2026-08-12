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

internal static class RunnerFingerprint
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
    internal static string ContentHash
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
    internal static string ComputeContentHash(string assemblyLocation)
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
    ///
    /// Callers must have already resolved the BC version (true for every existing call
    /// site — version selection happens once at startup, before any bundle/dependency
    /// compile). This is enforced, not just documented: reading
    /// <see cref="BcArtifacts.SelectedVersion"/> here for the first time would trigger
    /// BcArtifacts' lazy latest-in-cache default, silently keying a cache entry to
    /// whichever version happened to be latest-in-cache rather than the run's actual
    /// selection — exactly the finding-3 poisoning this type exists to prevent, one call
    /// site earlier. <see cref="BcArtifacts.IsSelected"/> exists precisely so a caller can
    /// check without triggering that lazy default, so this throws instead of silently
    /// defaulting.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The BC version has not been selected yet (<see cref="BcArtifacts.IsSelected"/> is
    /// false). Select it (<c>BcArtifacts.SelectVersion</c>) before computing any cache key.
    /// </exception>
    ///
    /// <remarks>
    /// <para><b>Is runner-content + bc:&lt;version&gt; the complete set of "what changes
    /// codegen but isn't in the runner's own bytes"?</b> Audited deliberately, since this
    /// PR's whole thesis is that an under-capturing fingerprint used to be masked by an
    /// accidental mtime invalidation — that masking is gone now, so anything left out here
    /// is a genuine stale-hit risk, not a free miss.</para>
    /// <list type="bullet">
    /// <item><description><b>Roslyn / Mono.Cecil / other NuGet-referenced DLLs</b>
    /// (<c>Microsoft.CodeAnalysis.CSharp</c>, used by the emit/compile path itself) are
    /// separate bin-deployed files, not inside al-runner.dll's own bytes — but they're
    /// exact-pinned <c>PackageReference</c> versions in <c>AlRunner.csproj</c>, and any
    /// version bump is stamped into al-runner.dll's own AssemblyRef metadata by the
    /// compiler, so it already changes <see cref="ContentHash"/> without needing a
    /// separate line. Covered.</description></item>
    /// <item><description><b>The Cecil-rewritten Ncl.dll content</b> does not need a line:
    /// the rewrite patches method BODIES only (see precompiled-dll-respect.md — the public
    /// surface an AL-output bundle compiles against is untouched), so it cannot affect the
    /// bytes stored in the AL-output cache, only the runtime behaviour of Ncl when the
    /// cached bundle is later loaded and run — which is gated by ncl-cecil's own separate
    /// key at every process startup, independent of whether al-out HITs or MISSes. A stale
    /// rewrite is an ncl-cecil bug, not something an al-out HIT could mask.</description></item>
    /// <item><description><b>Environment variables</b>: audited every
    /// <c>Environment.GetEnvironmentVariable</c> read reachable from the emit/compile path
    /// (<c>AL_RUNNER_EMIT_TIMEOUT_SEC</c>, <c>BCCOMPILER_TIMING</c>/<c>_DIAG</c>/
    /// <c>_TRACE</c>/<c>_DUMP_CS</c>, <c>AL_RUNNER_ENABLE_R2R</c>/<c>_R2R_REEXECED</c>).
    /// All are diagnostics, timing output, a debug dump-to-disk side effect, or control
    /// the runner PROCESS's own R2R execution mode — none alter what bytes
    /// <c>compilation.Emit</c> produces. None found that qualify.</description></item>
    /// <item><description><b>BC artifact content vs. the version string</b> — this is the
    /// one real gap, narrow and CI-safe. <see cref="BcArtifacts.SelectedVersion"/> is
    /// always the FULL four-part version (<c>System.Version.Parse</c> of the matched
    /// artifact directory's name) for the standard <c>--bc-version</c> path, which this
    /// repo's CI always uses (<c>test-matrix.yml</c>) — official artifacts are immutable
    /// per that exact four-part number in practice, so <c>bc:&lt;version&gt;</c> is
    /// sufficient there. But <c>--artifact-path</c> (an explicit dev-workflow override that
    /// bypasses the standard cache and any hash verification — see
    /// <c>BcArtifacts.VersionFromArtifactRoot</c>) falls back to reading the Ncl.dll's own
    /// <c>AssemblyName.Version</c> when the pointed-at directory's name doesn't parse as a
    /// version — and that assembly version is DELIBERATELY coarsened to
    /// <c>MAJOR.0.0.0</c> (see <c>BcArtifacts.cs</c>'s major-only compatibility check
    /// comment). Two different <c>--artifact-path</c> directories for the same BC major
    /// version, both named something other than a parseable version string, would collide
    /// on the same <c>bc:</c> line despite potentially different actual DLL content. Not
    /// fixed here: CI never exercises this path, and hashing an entire artifact directory
    /// on every cache-key computation is a real cost for a dev-only edge case. Flagged so
    /// it's a documented, deliberate gap rather than a silent one.</description></item>
    /// </list>
    /// </remarks>
    internal static void WriteKeyLines(Action<string> writeLine)
    {
        RequireBcVersionSelected(BcArtifacts.IsSelected);
        WriteKeyLines(writeLine, ContentHash, BcArtifacts.SelectedVersion);
    }

    /// <summary>
    /// The guard itself, factored out to take <paramref name="isSelected"/> as a plain
    /// bool rather than reading <see cref="BcArtifacts.IsSelected"/> internally. Once any
    /// test in a shared process selects a BC version, <see cref="BcArtifacts"/>' selection
    /// is ambient process-global state that cannot be unset — so a test cannot reliably
    /// force <see cref="BcArtifacts.IsSelected"/> back to false to exercise this throw.
    /// Taking the bool as a parameter makes the guard's logic testable in isolation
    /// without touching that shared state at all.
    /// </summary>
    internal static void RequireBcVersionSelected(bool isSelected)
    {
        if (!isSelected)
            throw new InvalidOperationException(
                $"{nameof(RunnerFingerprint)}.{nameof(WriteKeyLines)}: BC version not yet selected " +
                $"(BcArtifacts.IsSelected is false). Reading BcArtifacts.SelectedVersion here would " +
                "trigger its lazy latest-in-cache default and key this cache entry to the wrong BC " +
                "version instead of the run's actual selection — call BcArtifacts.SelectVersion first.");
    }

    /// <summary>
    /// Testable core of <see cref="WriteKeyLines(Action{string})"/>: takes the content
    /// hash and BC version explicitly so a test can vary either independently without
    /// needing to swap out the running assembly or the process-global BC version
    /// selection.
    /// </summary>
    internal static void WriteKeyLines(Action<string> writeLine, string contentHash, Version bcVersion)
    {
        writeLine($"runner:{contentHash}");
        writeLine($"bc:{bcVersion}");
    }
}
