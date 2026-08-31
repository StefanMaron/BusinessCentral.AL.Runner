namespace AlRunner.Infrastructure;

/// <summary>
/// Single source of truth for the BC service-tier artifact directory and the
/// process-global selected BC version.
///
/// The runner downloads BC platform DLLs (Ncl/Types/Common/Language/CodeAnalysis +
/// their runtime closure) to <c>~/.local/share/al-runner/artifacts/&lt;bc-version&gt;/</c>.
/// The exact version is pinned by <c>AlRunner.csproj</c>'s <c>_BCVersion</c> at build
/// time (those 5 DLLs are <c>&lt;Reference&gt;</c>d and CopyLocal'd into bin/), so the
/// *engine* a given binary runs is fixed at build time. But the runtime artifact /
/// symbol / dependency resolvers must agree on a *single* selected version, and that
/// version may be overridden (<c>--bc-version</c>, <c>--artifact-path</c>) or default
/// to the latest version present in the cache.
///
/// <para>Three independent resolvers consume the selection:</para>
/// <list type="bullet">
///   <item>engine/source artifact dir — <see cref="ServiceTierDir"/> (this file)</item>
///   <item>dependency package-cache dirs — <c>Program.DefaultPackageCacheDirs</c></item>
///   <item>compile symbol dirs — <c>BcCompiler.ResolveSymbolDirs</c></item>
/// </list>
/// They all read <see cref="SelectedVersion"/> so a single selection drives the run.
///
/// <para>No auto-download: when an artifact root is missing or empty we throw a loud
/// error naming the explicit download command rather than silently falling back to a
/// non-existent <c>0.0.0.0</c> path (that produced confusing downstream load failures).</para>
/// </summary>
public static class BcArtifacts
{
    public const string ArtifactsRoot_Rel = ".local/share/al-runner/artifacts";

    /// <summary>
    /// The tool-install-valid, one-command fix — works identically whether the runner
    /// came from `dotnet tool install` or a source checkout. This is the PRIMARY
    /// recommendation in every provisioning-gap message: unlike the old
    /// `tools/DownloadArtifacts` fallback, it names no path that only exists in this
    /// repo's own source tree.
    /// </summary>
    public static string ProvisionHint(string? versionPrefix = null)
        => versionPrefix == null
            ? "al-runner provision   (or re-run your command with --auto-provision)"
            : $"al-runner provision --bc-version {versionPrefix}   (or re-run your command with --auto-provision)";

    /// <summary>
    /// The explicit, force-a-specific-download fallback: `provision --service-tier`
    /// (issue #2085). Unlike the old `dotnet run --project tools/DownloadArtifacts --
    /// service-tier` this replaced — which required a source checkout of this repo, so it
    /// was a dead end for anyone who reached it via `dotnet tool install` — this is a
    /// subcommand on the SAME binary printing the message, so it always works. Bypasses
    /// need-detection and resolves its own canonical output directory, so no directory
    /// argument is needed here (kept as a parameter for the version only).
    /// </summary>
    public static string DownloadCommand(System.Version ver)
        => $"al-runner provision --service-tier --bc-version {ver} --force";

    private static System.Version? _selectedVersion;
    private static string? _selectedRoot;

    /// <summary>
    /// The single BC version selected for this process (set once at startup, default =
    /// latest in the artifacts cache). Resolvers A/B/C all read this so they agree.
    /// Reading before <see cref="SelectVersion"/> triggers lazy default selection.
    /// </summary>
    public static System.Version SelectedVersion
    {
        get { EnsureSelected(); return _selectedVersion!; }
    }

    /// <summary>The engine/source artifact dir for the selected version.</summary>
    public static string ServiceTierDir
    {
        get { EnsureSelected(); return _selectedRoot!; }
    }

    /// <summary>Whether a version has been selected, WITHOUT triggering the lazy
    /// default selection. Lets early-installed resolvers decide if probing the
    /// artifact dir is safe yet (probing before selection would silently commit
    /// the process to latest-in-cache).</summary>
    public static bool IsSelected => _selectedVersion != null;

    private static string _selectedCountry = "w1";

    /// <summary>
    /// The single BC artifact country/localization channel selected for this process
    /// (issue #2236) — "w1" (worldwide, the default) or a two/three-letter BC country
    /// code such as "us", "de", "gb". Set once at startup from <c>--country</c>
    /// (Program.cs); every resolver that needs a country-keyed directory or CDN channel
    /// reads this instead of taking the value as a parameter, so <c>--country</c> does
    /// not have to be threaded through every call site individually — the same pattern
    /// <see cref="SelectedVersion"/> already uses for the BC version. Deliberately a
    /// plain mutable property, not lock-guarded like <see cref="SelectVersion"/>: unlike
    /// version selection there is no directory to probe and no "already selected,
    /// conflicting re-select" hazard — it is just a string the download/path helpers
    /// read, validated lazily by whether the resulting CDN URL 404s (see
    /// ArtifactDownloader.PlatformApps), not by an allowlist here.
    /// </summary>
    public static string SelectedCountry
    {
        get => _selectedCountry;
        set => _selectedCountry = string.IsNullOrWhiteSpace(value) ? "w1" : value.Trim().ToLowerInvariant();
    }

    private static readonly object _lock = new();

    // AlRunnerPaths.UserHome throws loudly (issue #2114) rather than silently handing back
    // a relative path when $HOME names a directory that does not exist.
    private static string ArtifactsRoot => Path.Combine(AlRunnerPaths.UserHome, ArtifactsRoot_Rel);

    /// <summary>The per-user artifacts root (<c>~/.local/share/al-runner/artifacts</c>),
    /// where each BC version lives in a version-named subdir. Public for the provisioning
    /// flow, which downloads into <see cref="ArtifactDirFor"/> before selection runs.</summary>
    public static string ArtifactsRootDir => ArtifactsRoot;

    /// <summary>The artifact directory for a specific full version string.</summary>
    public static string ArtifactDirFor(string version) => Path.Combine(ArtifactsRoot, version);

    /// <summary>
    /// Explicitly select the BC version used by every resolver this process.
    /// Call once at startup BEFORE any resolver runs. Idempotent: a second call with
    /// the same effective selection is a no-op; a conflicting call throws.
    /// </summary>
    /// <param name="requestedVersionOrNull">version prefix (e.g. "28.1") or full
    /// version; null = latest in the artifacts cache.</param>
    /// <param name="explicitRootOrNull">explicit artifact root (dir containing
    /// platform/ + w1/) bypassing the cache scan; its version is read from the dir
    /// name or the contained Ncl.dll.</param>
    public static void SelectVersion(string? requestedVersionOrNull, string? explicitRootOrNull)
    {
        lock (_lock)
        {
            if (explicitRootOrNull != null)
            {
                if (!Directory.Exists(explicitRootOrNull))
                    throw new InvalidOperationException(
                        $"--artifact-path: directory does not exist: {explicitRootOrNull}");
                var ver = VersionFromArtifactRoot(explicitRootOrNull);
                var canonical = Path.GetFullPath(explicitRootOrNull)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Assign(ver, canonical);
                return;
            }

            var root = ArtifactsRoot;
            var dir = SelectArtifactVersionDir(root, requestedVersionOrNull);
            var v = System.Version.Parse(Path.GetFileName(dir));
            Assign(v, dir);
        }
    }

    private static void Assign(System.Version v, string dir)
    {
        if (_selectedVersion != null)
        {
            if (_selectedVersion == v && string.Equals(_selectedRoot, dir, StringComparison.Ordinal))
                return; // idempotent
            throw new InvalidOperationException(
                $"BC version already selected ({_selectedVersion} at {_selectedRoot}); " +
                $"cannot re-select {v} at {dir}.");
        }
        _selectedVersion = v;
        _selectedRoot = dir;
    }

    private static void EnsureSelected()
    {
        if (_selectedVersion != null) return;
        lock (_lock)
        {
            if (_selectedVersion != null) return;
            var root = ArtifactsRoot;
            var dir = SelectArtifactVersionDir(root, null);
            _selectedVersion = System.Version.Parse(Path.GetFileName(dir));
            _selectedRoot = dir;
        }
    }

    /// <summary>
    /// List immediate child dirs of <paramref name="rootDir"/>, parse each as a
    /// <see cref="System.Version"/> (skipping non-version names), and return the
    /// highest. If <paramref name="requestedVersionOrNull"/> is supplied, return the
    /// highest whose version STARTS WITH the requested prefix (so "27.5" matches
    /// "27.5.46862.48827", and a full version matches exactly).
    /// Throws loudly (naming the explicit download command) when the root is missing /
    /// empty or no version matches the request — never a silent fallback.
    /// </summary>
    public static string SelectArtifactVersionDir(string rootDir, string? requestedVersionOrNull)
    {
        if (!Directory.Exists(rootDir))
            throw new InvalidOperationException(
                $"BC artifact root not found: {rootDir}. No artifacts are downloaded — resolve it ONE of these ways: " +
                $"(a) {ProvisionHint()}; " +
                $"(b) {DownloadCommand(new System.Version(28, 1))}");

        var candidates = Directory.EnumerateDirectories(rootDir)
            .Select(d => (Dir: d, Name: Path.GetFileName(d),
                          Ver: System.Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Ver != null)
            .OrderByDescending(t => t.Ver)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"BC artifact root {rootDir} contains no version-named directories. Resolve it ONE of these ways: " +
                $"(a) {ProvisionHint()}; " +
                $"(b) {DownloadCommand(new System.Version(28, 1))}");

        if (requestedVersionOrNull == null)
            return candidates[0].Dir;

        var prefix = requestedVersionOrNull.Trim();
        // Exact-version match first; else highest whose dot-segmented name starts with
        // the requested prefix segments (so "27.5" matches "27.5.x", but not "27.50").
        var match = candidates.FirstOrDefault(t => VersionNameMatchesPrefix(t.Name, prefix));
        if (match.Dir == null)
        {
            var available = string.Join(", ", candidates.Select(t => t.Name));
            throw new InvalidOperationException(
                $"No BC artifact under {rootDir} matches version '{requestedVersionOrNull}'. " +
                $"Available: {available}. Resolve it ONE of these ways: " +
                $"(a) {ProvisionHint(prefix)}; " +
                $"(b) {DownloadCommand(System.Version.TryParse(EnsureFourPart(prefix), out var pv) ? pv : new System.Version(28, 1))}");
        }
        return match.Dir;
    }

    // "27.5" matches "27.5.46862.48827"; "28.1.49838.50794" matches itself; "27.50"
    // does NOT match "27.5.x". Segment-wise prefix on the dotted name.
    private static bool VersionNameMatchesPrefix(string name, string prefix)
    {
        if (string.Equals(name, prefix, StringComparison.Ordinal)) return true;
        var ns = name.Split('.');
        var ps = prefix.Split('.');
        if (ps.Length > ns.Length) return false;
        for (int i = 0; i < ps.Length; i++)
            if (!string.Equals(ns[i], ps[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string EnsureFourPart(string prefix)
    {
        var parts = prefix.Split('.');
        var list = new List<string>(parts);
        while (list.Count < 4) list.Add("0");
        return string.Join('.', list.Take(4));
    }

    /// <summary>
    /// If <paramref name="artifactPath"/> is a version-named directory directly under the
    /// standard artifacts root, return its full version string (so the caller can route it
    /// through the normal <c>--bc-version</c> selection). Returns null when the path is
    /// outside the standard cache (then the explicit-root branch handles it). Throws if the
    /// path does not exist or its version cannot be determined.
    /// </summary>
    public static string? TryTranslateArtifactPathToVersion(string artifactPath)
    {
        if (!Directory.Exists(artifactPath))
            throw new InvalidOperationException(
                $"--artifact-path: directory does not exist: {artifactPath}");
        var ver = VersionFromArtifactRoot(artifactPath);
        var canonical = Path.GetFullPath(artifactPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var standardChild = Path.Combine(ArtifactsRoot, ver.ToString());
        return string.Equals(canonical, standardChild, StringComparison.Ordinal)
            ? ver.ToString()
            : null;
    }

    private static System.Version VersionFromArtifactRoot(string root)
    {
        // Prefer the dir name if it parses as a version; else read the Ncl.dll inside.
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        if (System.Version.TryParse(name, out var byName)) return byName;

        var ncl = Path.Combine(root, "Microsoft.Dynamics.Nav.Ncl.dll");
        if (File.Exists(ncl))
        {
            var v = System.Reflection.AssemblyName.GetAssemblyName(ncl).Version;
            if (v != null) return v;
        }
        throw new InvalidOperationException(
            $"--artifact-path: cannot determine BC version from {root} " +
            $"(dir name is not a version and no Microsoft.Dynamics.Nav.Ncl.dll present).");
    }

    /// <summary>
    /// Startup consistency check: the engine DLL (Ncl) baked into bin/ is built for a
    /// specific BC version. If the selected artifact/dependency version has a different
    /// MAJOR, the dependency symbols and the engine disagree at the API level — fail loud.
    ///
    /// We compare MAJOR only: BC pins its assembly version at <c>MAJOR.0.0.0</c>
    /// regardless of the product/file version (the 28.1.x artifact ships Ncl with
    /// AssemblyName.Version = 28.0.0.0), so minor/patch skew (28.1.x build vs 28.1.y
    /// cache, or a 28.0-stamped assembly inside a 28.1 artifact) is expected and tolerated.
    /// </summary>
    /// <summary>
    /// The BC MAJOR version the engine (bin Ncl.dll) was built for, or null when the
    /// engine DLL is absent / unversioned. This is the only major this binary can run
    /// (cross-major needs a matching engine build); used to default artifact selection.
    /// </summary>
    public static int? EngineMajor(string binDir) => EngineVersion(binDir)?.Major;

    /// <summary>
    /// Version of bin Ncl.dll. NOTE: Microsoft stamps this as major.0.0.0 — the MINOR is
    /// always 0 and carries no information. Use <see cref="EngineBuiltVersion"/> when the
    /// minor matters (i.e. whenever selecting an artifact).
    /// </summary>
    public static Version? EngineVersion(string binDir)
    {
        var ncl = Path.Combine(binDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        if (!File.Exists(ncl)) return null;
        return System.Reflection.AssemblyName.GetAssemblyName(ncl).Version;
    }

    /// <summary>
    /// The full 4-part BC version this binary was BUILT against, baked in at compile time
    /// from the csproj `_BCVersion` property (see the AssemblyMetadata item there). This is
    /// the only place the built MINOR survives into the shipped binary — Ncl.dll's own
    /// assembly version is major.0.0.0. Null if the attribute is missing or unparseable.
    /// </summary>
    public static Version? EngineBuiltVersion()
    {
        var attrs = typeof(BcArtifacts).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
        foreach (System.Reflection.AssemblyMetadataAttribute a in attrs)
            if (a.Key == "BcEngineVersion" && Version.TryParse(a.Value, out var v))
                return v;
        return null;
    }

    /// <summary>
    /// The version prefix to select by when the user pinned neither --bc-version nor
    /// --artifact-path, in descending order of tightness: the engine's EXACT built
    /// version when it is cached, else its MAJOR.MINOR when that minor is cached, else
    /// the major alone. Null when the engine version is unknown (caller decides).
    ///
    /// Why the exact build and not just major.minor: two BUILDS of the same BC minor ship
    /// different Microsoft.Dynamics.Nav.CodeAnalysis assembly versions (28.1.49838.50794 ->
    /// 17.0.36.40629, 28.1.49838.53220 -> 17.0.39.53543), and bin/ CopyLocal's the one it
    /// was compiled against. The strong-named reference does not tolerate that skew, so
    /// picking highest-in-minor kills the process at startup with FileLoadException
    /// 0x80131621 before any test runs. Same lesson as the major.minor tier below, one
    /// level deeper.
    ///
    /// Why major.minor and not just major: MEASURED 2026-07-29 on Pageworks (same dep set,
    /// same binary, engine built for 28.1.49838.50794) —
    ///   selected 28.1.x -> 1041 pass / 35 fail / 0 error
    ///   selected 28.2.x ->  996 pass / 77 fail / 3 error
    /// The 42 regressions were all TestPage subpage-part resolution failures. Latest-in-major
    /// silently picked 28.2 whenever any 28.2 artifact happened to be cached, so the runner
    /// chose a degraded configuration on its own and said nothing.
    /// </summary>
    public static string? DefaultVersionPrefix(Version? engineVersion, string artifactsRoot)
    {
        if (engineVersion == null) return null;

        // Tier 1: the exact 4-part build. Only this artifact's CodeAnalysis.dll matches
        // the strong-named reference baked into bin/.
        var exact = engineVersion.ToString();
        try
        {
            SelectArtifactVersionDir(artifactsRoot, exact);
            return exact;
        }
        catch (InvalidOperationException)
        {
            // Not cached — fall through to the looser tiers.
        }

        var majorMinor = $"{engineVersion.Major}.{engineVersion.Minor}";
        try
        {
            SelectArtifactVersionDir(artifactsRoot, majorMinor);
            return majorMinor;
        }
        catch (InvalidOperationException)
        {
            // Engine's own minor isn't cached — fall back to the major, which is still the
            // only major this binary can run. Degraded; the caller warns.
            return engineVersion.Major.ToString();
        }
    }

    /// <summary>
    /// Pure core of <see cref="DefaultProvisionTarget"/>, network-injectable for testing
    /// (see AlRunner.Tests.DefaultProvisionTargetTests) — same shape as
    /// <see cref="DescribeExplicitEngineMinorMismatch"/>/<see cref="WarnIfExplicitEngineMinorMismatch"/>:
    /// a provable pure function plus a thin real-network wrapper.
    ///
    /// Issue #2033: <see cref="DefaultVersionPrefix"/> answers "what does the LOCAL CACHE
    /// already have" — the right question when there is no provisioning step coming (offline
    /// / --no-auto-provision), but the wrong one when auto-provisioning is about to run
    /// anyway. On a genuinely empty cache it collapses straight to "major only" before a
    /// single byte has been fetched, and the caller then asked provisioning for "the newest
    /// build of major 28" instead of "BC 28.1, the engine's own minor" — landing on 28.4 while
    /// the engine was built for 28.1. This variant tries the SAME three tiers (exact build,
    /// then minor, then major) but at each tier also asks whether the CDN can fetch it before
    /// giving up and falling looser, so auto-provisioning downloads what version selection
    /// actually wants instead of "whatever happens to already be cached."
    /// </summary>
    /// <param name="tier">
    /// Which tier won: "cached-exact"/"cached-minor" (already local, nothing to fetch,
    /// identical to <see cref="DefaultVersionPrefix"/>'s outcome), "cdn-exact"/"cdn-minor"
    /// (not cached but the CDN has it — provisioning will fetch exactly this), or
    /// "major-fallback" (neither the engine's exact build nor its minor is available from
    /// either source — genuinely degraded; the caller must warn, per issue #2020).
    /// </param>
    public static string ResolveProvisionTargetCore(Version engineVersion, string artifactsRoot,
        Func<string, bool> cdnHasExactVersion, Func<string, string?> cdnResolvePrefix, out string tier)
    {
        // Tier 1: the exact 4-part build — cached, or fetchable from the CDN.
        var exact = engineVersion.ToString();
        try
        {
            SelectArtifactVersionDir(artifactsRoot, exact);
            tier = "cached-exact";
            return exact;
        }
        catch (InvalidOperationException) { /* not cached — try the CDN, then fall through */ }
        if (cdnHasExactVersion(exact))
        {
            tier = "cdn-exact";
            return exact;
        }

        // Tier 2: the engine's own minor — cached, or resolvable to a full build via the CDN.
        var majorMinor = $"{engineVersion.Major}.{engineVersion.Minor}";
        try
        {
            SelectArtifactVersionDir(artifactsRoot, majorMinor);
            tier = "cached-minor";
            return majorMinor;
        }
        catch (InvalidOperationException) { /* not cached — try the CDN, then fall through */ }
        var minorResolved = cdnResolvePrefix(majorMinor);
        if (minorResolved != null)
        {
            tier = "cdn-minor";
            return minorResolved;
        }

        // Tier 3: neither the exact build nor the engine's own minor is available from
        // either source (e.g. Microsoft withdrew the build — #2010). Fall back to the bare
        // major; the caller resolves+downloads the latest build of it and must warn loud —
        // this is the one genuinely degraded outcome, not the default-path norm.
        tier = "major-fallback";
        return engineVersion.Major.ToString();
    }

    /// <summary>
    /// Real-network wrapper around <see cref="ResolveProvisionTargetCore"/> — the one
    /// Program.cs calls on the auto-provision default path (issue #2033).
    /// </summary>
    public static string DefaultProvisionTarget(Version engineVersion, string artifactsRoot, out string tier,
        Action<string>? log = null)
        => ResolveProvisionTargetCore(engineVersion, artifactsRoot,
            v => AlRunner.Provisioning.ArtifactDownloader.VersionExists(v, log),
            p => AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(p, log),
            out tier);

    public static void VerifyEngineConsistency(string binDir)
    {
        var ncl = Path.Combine(binDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        if (!File.Exists(ncl)) return; // nothing to compare against
        var engineVer = System.Reflection.AssemblyName.GetAssemblyName(ncl).Version;
        if (engineVer == null) return;

        var selected = SelectedVersion;
        if (engineVer.Major != selected.Major)
        {
            throw new InvalidOperationException(
                $"BC engine/version mismatch: this binary was built for engine major " +
                $"{engineVer.Major} (bin Ncl.dll = {engineVer}) but the selected " +
                $"BC version is {selected} (major {selected.Major}). Rebuild with " +
                $"-p:_BCVersion={selected} or select major {engineVer.Major} " +
                $"(--bc-version {engineVer.Major}).");
        }
    }

    /// <summary>
    /// Pure core of <see cref="WarnIfExplicitEngineMinorMismatch"/>, split out for direct unit
    /// testing (see AlRunner.Tests.EngineMinorMismatchWarningTests) — mirrors the
    /// BcEngineReadinessGuard.AssertReadyOnCi(bool,string?,bool) shape: a pure function over
    /// explicit values is provable with no BC engine or CLI invocation involved.
    ///
    /// #2008's actual root cause: a shipped al-runner binary's engine (Ncl.dll etc.) is built
    /// for one BC MINOR (the release pipeline pins BC 28.1 as `required-version` — see
    /// bc-tests.yml), but Ncl.dll's own AssemblyVersion is always MAJOR.0.0.0, so
    /// VerifyEngineConsistency (which only compares Major) cannot see a same-major
    /// different-minor selection at all. Running that binary with `--bc-version 28.3`
    /// silently ran BC 28.1's engine against BC 28.3 artifacts and threw a
    /// NullReferenceException deep inside BC's own FieldDataProvider ctor
    /// (NavGlobal.get_SystemTenant → get_NCLMetadata) with nothing pointing at the real
    /// cause. The runner ALREADY had this exact warning — Program.cs's auto-select default
    /// path prints it when the user passes neither --bc-version nor --artifact-path — but it
    /// was unreachable in precisely the case that needed it: an EXPLICIT --bc-version/
    /// --artifact-path skips the auto-select branch entirely. This is the same warning,
    /// reachable from the explicit-selection path too.
    /// </summary>
    internal static string? DescribeExplicitEngineMinorMismatch(System.Version? builtVersion, System.Version selectedVersion)
    {
        if (builtVersion == null) return null; // older/unstamped binary — nothing to compare
        if (builtVersion.Major == selectedVersion.Major && builtVersion.Minor == selectedVersion.Minor)
            return null; // matched minor, or patch-level skew within it — tolerated, same as VerifyEngineConsistency

        return $"[bc] warning: this binary's engine was built for BC {builtVersion} but BC {selectedVersion} was " +
            $"explicitly selected (--bc-version/--artifact-path) — different minor is a KNOWN-DEGRADED " +
            $"configuration (measured: dozens of extra failures from engine/artifact minor skew, see #2008). " +
            $"Fix with one of: drop --bc-version so the runner auto-selects its own engine's minor " +
            $"({builtVersion.Major}.{builtVersion.Minor}), or rebuild with -p:_BCVersion={selectedVersion}.";
    }

    /// <summary>
    /// Called ONLY when the user explicitly chose the BC version (--bc-version or
    /// --artifact-path) — never from the auto-select default path, which already prints its
    /// own equivalent warning inside Program.cs and must not be double-warned. See
    /// DescribeExplicitEngineMinorMismatch for the full rationale.
    /// </summary>
    public static void WarnIfExplicitEngineMinorMismatch()
    {
        var msg = DescribeExplicitEngineMinorMismatch(EngineBuiltVersion(), SelectedVersion);
        if (msg != null) Console.Error.WriteLine(msg);
    }

    /// <summary>
    /// Issue #2210: the auto-select default path's cross-major note body — describes the
    /// case where the target project's app.json (application/platform) declares a BC major
    /// different from the one this engine build actually resolves Base/System Application
    /// symbols against. Callers prepend their own tag (e.g. "[bc] note: ", "[provision]
    /// note: ") — this returns only the message body, no tag, so the two call sites (the
    /// main auto-select path and al-runner provision's ResolveDefaultProvisionVersion) don't
    /// double-tag it.
    ///
    /// This is NOT the same risk as <see cref="DescribeExplicitEngineMinorMismatch"/> above.
    /// That one is a real engine/artifact DLL-version skew (VerifyEngineConsistency's own
    /// MAJOR-only check already refuses a genuine cross-major engine/artifact pairing before
    /// this point ever runs) — a live compatibility hazard, measured at dozens of extra
    /// failures. This one compares the app's DECLARED MINIMUM version against whatever major
    /// actually ran it, which are never in tension with each other: AL's `application`/
    /// `platform` fields are minima, not pins (see BcVersionFloorSkipTests), and BC itself is
    /// designed so an app declaring an older minimum runs unmodified on a newer major — that
    /// is the entire point of the floor semantics, not a degraded corner case of them.
    ///
    /// Measured directly for #2210: the same AL source (tests/runner-extras/microsoft-
    /// dependencies, which exercises real Base Application/System Application codeunits —
    /// No. Series, Environment Information, Workflow Setup, Conf./Personalization Mgt., a
    /// RecordRef-filtered Base App table read) produced IDENTICAL 14/14 pass results whether
    /// resolved against a BC-27-built engine (matching the app's declared major) or a
    /// BC-28-built engine (one major ahead of it) — no divergence found. The pre-existing
    /// BcVersionFloorSkipTests healthy-suite fixture (application/platform "1.0.0.0") already
    /// runs this exact declared-vs-actual mismatch on EVERY supported BC major in CI
    /// (27.0-28.4) continuously, and always passes.
    ///
    /// Because there is no measured divergence risk and BC's own floor semantics say this is
    /// the expected case (not a degraded one), it is NOT printed at default verbosity at all
    /// — both call sites gate it on Log.Verbose. A condition that fires on most runs of an
    /// affected project and then passes teaches users to skim past everything the runner
    /// prints, including the warnings that matter; it stays available, on request, to anyone
    /// chasing exact-major parity. The message itself stays short because it is no longer
    /// competing for attention on every run — the full measurement and reasoning live in
    /// this doc comment instead.
    ///
    /// Returns null when there is nothing to say (no derivable project major, or majors
    /// already match).
    /// </summary>
    public static string? DescribeCrossMajorNote(string? projMajor, int engineMajor)
    {
        if (projMajor == null || projMajor == engineMajor.ToString()) return null;
        return $"project app.json declares BC major {projMajor}; this run resolves against " +
            $"major {engineMajor} instead (a minimum, not a pin — measured no behavior " +
            $"difference for real Base/System Application logic, see #2210). Install a " +
            $"runner build for major {projMajor} (-p:_BCVersion=...) for an exact match.";
    }

    /// <summary>
    /// Issue #2037: whether Program.cs should even CALL <see cref="WarnIfExplicitEngineMinorMismatch"/>
    /// at all, given how many per-BC-minor engine variants this install ships (see
    /// <see cref="EngineVariants.Discover"/>, called just before the variant-swap block in
    /// Program.cs — the same input, so no rediscovery is needed at the call site).
    ///
    /// The warning compares THIS PROCESS's own compiled-in engine minor against the
    /// selected version — a comparison that stopped being meaningful the moment a packaged
    /// install started shipping more than one engine (#2027 / #2035): once <em>any</em>
    /// variant is shipped, the variant-selection block downstream is the sole authority —
    /// either a matching variant is found (this process is about to swap into the right
    /// engine, making the warning's claim false, as reported live against the published
    /// 2.5.0 package with `--bc-version 27.5.46862.53931`) or none matches (that block itself
    /// exits with a sharper, version-naming error before a generic warning would even help).
    /// So the warning has nothing correct left to say once <paramref name="shippedVariantCount"/>
    /// is nonzero. It is only still true for a single-build install with no variants/
    /// directory at all (shippedVariantCount == 0) — the case #2008 was filed against, which
    /// must stay reachable.
    /// </summary>
    public static bool ShouldWarnExplicitEngineMinorMismatch(bool bcVersionAutoSelected, int shippedVariantCount)
        => !bcVersionAutoSelected && shippedVariantCount == 0;
}
