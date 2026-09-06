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
// #2818: "stable across rebuilds of unchanged source" holds only because the build is
// configured to make it hold, and that configuration lives in a different file. Two SDK
// behaviours embed the git commit into these bytes (#1881) and a third embeds the build
// machine's absolute paths — the PDB path in the CodeView debug-directory entry, plus the
// deterministic PDB ID and MVID, which hash the source documents' absolute paths. Measured:
// two git worktrees of a byte-identical tree, same SDK, same machine, same build command,
// produced two different al-runner.dll hashes, so two checkouts of one commit shared nothing
// they cached. All three are switched off in the repo-root Directory.Build.props
// (IncludeSourceRevisionInInformationalVersion, SuppressImplicitGitSourceLink, PathMap) and
// guarded by AlRunner.Tests/BuildDeterminismTests.cs. Deleting any of them silently turns
// every persisted runner cache key back into a hash of WHERE and WHEN it was built.
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
            return UnknownContentHash;
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(assemblyLocation);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// The answer <see cref="ComputeContentHash"/> gives for a path it cannot read. Named
    /// rather than spelled out at each comparison, because a caller that keys a cache on
    /// this value gives every unidentifiable file ONE shared key — the exact wrong-answer
    /// shape content addressing exists to remove (#2955).
    /// </summary>
    internal const string UnknownContentHash = "unknown";

    /// <summary>
    /// Non-null when the runner cannot identify ITSELF — the reason the AL-output cache may
    /// claim no identity at all this run (#2954). See the SCOPE paragraph below for what that
    /// does and does not cover.
    ///
    /// <para><see cref="ContentHash"/> answers <see cref="UnknownContentHash"/> for an
    /// assembly with no readable on-disk location (a single-file publish, an in-memory host,
    /// the DLL unlinked underneath a running process). Every key that calls
    /// <see cref="WriteKeyLines(Action{string})"/> then writes the literal line
    /// <c>runner:unknown</c>, which is the SAME line for every runner build that ever lands
    /// in that state. That is one
    /// shared cache identity standing in for an input the runner could not read, so a warm
    /// entry written by one runner build is served to a different one: different rewriters,
    /// different patches, different emit, and nothing in the run says so.</para>
    ///
    /// <para>The answer is not a better term — there is no term, the input is unknown — it is
    /// to claim no cache identity at all. <see cref="AppLoader"/>'s manifest index (#2987) and
    /// its r2r-chunk cache (#2955) already refuse the sentinel for exactly this reason.</para>
    ///
    /// <para>SCOPE — this property does not govern every persisted key, and reading it as
    /// though it did would be the same defect #2954 is about, one level up: a name standing in
    /// for coverage it does not have. Measured, it has exactly ONE consumer,
    /// <see cref="ProgramSupport.AlOutputCacheBlocker"/>, which gates the AL-output cache
    /// (the CLI gate and the server-mode gate both reach it through there) whose key is
    /// <c>ProgramSupport.ComputeAlCacheKey</c>. The other three
    /// <see cref="WriteKeyLines(Action{string})"/> call sites still write
    /// <c>runner:unknown</c> into a persisted key with nothing consulting this property:
    /// <c>DependencyLoader.ComputeSourceDependencyCacheKeyCore</c>,
    /// <c>SiblingCompile.ComputeSourceWorkspaceKey</c> and
    /// <c>InstallBaselineDiskCache.BuildKeyText</c>. Extending the gate to those is a real
    /// behaviour change needing its own proof, not something #2954 did.</para>
    /// </summary>
    internal static string? UncacheableReason => UncacheableReasonFor(ContentHash);

    /// <summary>
    /// Testable core of <see cref="UncacheableReason"/>: takes the hash explicitly, because a
    /// test cannot make the RUNNING assembly location-less without unloading itself.
    /// </summary>
    internal static string? UncacheableReasonFor(string? contentHash) =>
        string.IsNullOrEmpty(contentHash) || contentHash == UnknownContentHash
            ? "the running al-runner assembly has no readable on-disk location, so every cache "
              + $"key this process computes would carry 'runner:{UnknownContentHash}' — one "
              + "shared identity for every runner build at once"
            : null;

    // Path -> content hash memo. Moved down here from BcAppSymbolCache (#1820) so every
    // layer that needs "which bytes is this file?" as a cache-key term shares ONE memo
    // instead of each growing its own (#2955): AppLoader.ExtractAllDllPaths hashes the same
    // dependency packages the bc-symbols cache and the AL-output key's dep terms hash, so a
    // package already hashed by either costs a dictionary lookup here rather than a second
    // full read of a 100+MB file.
    //
    // ComputeContentHash reads the WHOLE file; the call sites ask per virtual-table lookup,
    // per dependency and per cache key, so an unmemoized hash is a re-read each time.
    //
    // The memo key identifies the FILE, not the path: (device, inode, size, last-write) where
    // the platform can answer, falling back to (full path, length, last-write UTC).
    //
    // The stat half is #2987. "One entry per path, never invalidated" rested on "nothing in
    // this process writes to a dependency .app", and that premise does not hold for every
    // caller: InProcessAppPackager writes synthetic .app packages mid-run, and a --watch
    // process outlives a rebuild of one. A path-keyed memo answers the FIRST bytes it ever saw
    // for those, so a caller keying a persisted cache on it would consult the previous
    // package's entry — the wrong-answer shape content addressing exists to remove,
    // reintroduced one layer down. The stat is not trusted to identify CONTENT here (that is
    // the whole point of this method); it is only used to notice that the file has been
    // written since, which is exactly what a stat can tell you. A rewrite that lands on the
    // same length and mtime is one the memo will not notice — that is a memo, not a cache key,
    // and it lives and dies with the process, which is why the persisted keys built FROM this
    // value are the ones that have to be right.
    //
    // The device/inode half is #3036. `provision-bc` hard-links `~/.al-runner/platform-apps`
    // into the default artifacts directory and Program.cs scans both, so the same inodes
    // arrive here under two absolute paths — and neither the path nor a stat can see that, so
    // the second one was a full re-read of 122.5 MB, 98 MB of it Base Application, on every
    // invocation. FileIdentity answers "same file?" from the filesystem instead; see its
    // header for why (device, inode, size, mtime) cannot say "same file" about two files that
    // both exist, which is the only claim this memo makes. Size and mtime appear in BOTH
    // halves and do the same job in each: notice that the file has been written since.
    // A platform that cannot answer falls back to the stat key and behaves as it did before.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _fileContentHashes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="ComputeContentHash"/> memoized per FILE — the entry point every cache key
    /// that identifies a file by its content should use. Two paths that are hard links to one
    /// inode share an entry and are read once (#3036); two files that both exist never do,
    /// whatever their paths, lengths or timestamps say.
    /// </summary>
    internal static string ComputeFileContentHashMemoized(string path)
    {
        var fullPath = Path.GetFullPath(path);
        // The two key spaces are kept disjoint by the "ino|" / "path|" prefixes rather than by
        // an argument about what an absolute path can start with — a memo whose two keying
        // schemes could ever produce the same string is the wrong-answer shape this exists to
        // avoid, not a place to be clever.
        var memoKey = FileIdentity.TryGetStableKey(fullPath) ?? StatKey(fullPath);
        return _fileContentHashes.GetOrAdd(memoKey, _ =>
        {
            System.Threading.Interlocked.Increment(ref _contentHashComputations);
            return ComputeContentHash(fullPath);
        });
    }

    /// <summary>
    /// The fallback key for a file the filesystem cannot identify for us (no statx: Windows,
    /// macOS, an older libc) — #2987's (path, length, mtime).
    ///
    /// <para>A file we cannot stat at all memoizes under the bare path: ComputeContentHash is
    /// about to answer <see cref="UnknownContentHash"/> for it anyway, and a later call that
    /// CAN stat it gets a different key and recomputes rather than inheriting that answer.</para>
    /// </summary>
    private static string StatKey(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            return fi.Exists
                ? $"path|{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"
                : "path|" + fullPath;
        }
        catch { return "path|" + fullPath; }
    }

    private static int _contentHashComputations;

    /// <summary>Test-only: how many times this process has actually READ AND HASHED a file
    /// through the memo. The memo's whole job is that this stays below the number of calls,
    /// and a test that asserts on returned hashes alone cannot see the difference between
    /// one read and two — two paths to one file return the same hash either way.</summary>
    internal static int ContentHashComputationCountForTests => Volatile.Read(ref _contentHashComputations);

    /// <summary>Test-only: drops the memo, so a test that rewrites a file in place can
    /// observe what a fresh process would compute for it. Production code never needs
    /// this — see the memo's comment for why it is never invalidated in a real run.</summary>
    internal static void ClearFileContentHashMemoForTests() => _fileContentHashes.Clear();

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
    /// <c>_TRACE</c>/<c>_DUMP_CS</c>). All are diagnostics, timing output or a debug
    /// dump-to-disk side effect — none alter what bytes <c>compilation.Emit</c> produces.
    /// None found that qualify. (This list also covered <c>AL_RUNNER_ENABLE_R2R</c> and
    /// <c>AL_RUNNER_R2R_REEXECED</c>, which controlled the runner PROCESS's own R2R
    /// execution mode and likewise did not qualify; both were removed with the
    /// DOTNET_ReadyToRun=0 re-exec — see Program.cs.)</description></item>
    /// <item><description><b>BC artifact content vs. the version string</b> — this is the
    /// one real gap, narrow and CI-safe. <see cref="BcArtifacts.SelectedVersion"/> is
    /// always the FULL four-part version (<c>System.Version.Parse</c> of the matched
    /// artifact directory's name) for the standard <c>--bc-version</c> path, which this
    /// repo's CI always uses (<c>bc-tests.yml</c>) — official artifacts are immutable
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
