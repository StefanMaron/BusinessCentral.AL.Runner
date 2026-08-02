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

    /// <summary>The explicit download command users must run (no auto-download).</summary>
    public static string DownloadCommand(System.Version ver, string dir)
        => $"dotnet run --project tools/DownloadArtifacts -- service-tier {ver} \"{dir}\"";

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

    private static readonly object _lock = new();

    private static string ArtifactsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ArtifactsRoot_Rel);

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
                $"BC artifact root not found: {rootDir}. No artifacts are downloaded — " +
                $"download one explicitly, e.g.: {DownloadCommand(new System.Version(28, 1), rootDir)}");

        var candidates = Directory.EnumerateDirectories(rootDir)
            .Select(d => (Dir: d, Name: Path.GetFileName(d),
                          Ver: System.Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Ver != null)
            .OrderByDescending(t => t.Ver)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"BC artifact root {rootDir} contains no version-named directories. " +
                $"Download one explicitly, e.g.: {DownloadCommand(new System.Version(28, 1), rootDir)}");

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
                $"Available: {available}. Download it explicitly, e.g.: " +
                $"{DownloadCommand(System.Version.TryParse(EnsureFourPart(prefix), out var pv) ? pv : new System.Version(28, 1), rootDir)}");
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
}
