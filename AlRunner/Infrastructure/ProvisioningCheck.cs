using AlRunner.Provisioning;

namespace AlRunner.Infrastructure;

/// <summary>
/// Verifies that the selected BC version's engine artifact closure is COMPLETE, and —
/// when asked — auto-resolves it in-process by downloading the missing pieces from the
/// public BC artifact CDN.
///
/// Why this exists: <see cref="BcArtifacts.SelectVersion"/> already fails loud when the
/// artifact root or the requested version dir is entirely absent. This class covers the
/// subtler case: the version dir EXISTS but is incomplete (e.g. a partial /service/ closure
/// download). Since the version-agnostic engine (StripBcAppClosureFromCopyLocal) now serves
/// the BC-app external closure from this dir at runtime, a partial closure fails deep in a
/// FileLoadException instead of at the surface — so we check it up front.
///
/// Policy (per the runner's "no silent download" rule): a missing piece produces ONE loud,
/// detailed message naming every missing file, its exact expected path, the precise manual
/// download command, AND a single one-command auto-resolve (`al-runner provision` /
/// `--auto-provision`). The runner never downloads unless the user opts in.
/// </summary>
public static class ProvisioningCheck
{
    // The engine DLLs the runner binds directly (must be present in the artifact dir so the
    // ALC resolver and the Cecil rewrite can load them).
    private static readonly string[] CoreEngineDlls =
    {
        "Microsoft.Dynamics.Nav.Ncl.dll",
        "Microsoft.Dynamics.Nav.Types.dll",
        "Microsoft.Dynamics.Nav.Common.dll",
        "Microsoft.Dynamics.Nav.Language.dll",
        "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
    };

    // Sentinel of the BC-app external closure that the version-agnostic engine relies on
    // being served from the artifact dir (it was the exact DLL whose absence/skew produced
    // FileLoadException 0x80131621). Its presence signals the full /service/ closure landed.
    private const string ClosureSentinel = "Microsoft.Identity.ServiceEssentials.Core.dll";

    // ── Platform-app R2R check ────────────────────────────────────────────────
    // These Microsoft apps MUST be provided as R2R (publishedartifacts/*.dll) packages.
    // Their procedure bodies are external/native — BC compiles them from AL source produces
    // EMIT-ZERO (the emitter crashes on NavTypeKind issues). The runner defers to service-tier
    // DLL dispatch for their codeunits at runtime. When a symbol-only .app is found in the
    // package cache instead of the R2R runtime package, it is a PROVISIONING gap.
    public static readonly IReadOnlyList<string> KnownPlatformRuntimeApps = new[]
    {
        "System Application",
        "Base Application",
        "Business Foundation",
    };

    /// <summary>True if <paramref name="appName"/> is a known Microsoft platform runtime app.</summary>
    public static bool IsKnownPlatformRuntimeApp(string appName)
        => KnownPlatformRuntimeApps.Any(n =>
            string.Equals(n, appName, StringComparison.OrdinalIgnoreCase));

    // ── Platform symbol app "System" ──────────────────────────────────────────
    // The Microsoft platform symbols app (Name="System", objects 2000000000..2000001000)
    // is NOT an R2R runtime package and never will be — it ships symbol AL whose procedure
    // bodies are external/native (`_Internal` platform methods). Its Tier-3 source-compile
    // ALWAYS fails Roslyn (dozens of CS0103/CS1061), after which the dependency loader
    // falls back to service-tier DLL dispatch anyway. Skipping the doomed compile up front
    // is observably identical and saves ~14.5s per bundle (measured 2026-07-23, Pageworks).
    // Well-known AppId, stable across BC versions.
    public static readonly Guid PlatformSystemAppId = Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d");

    /// <summary>
    /// True if the app is Microsoft's platform symbols app "System" — matched by its
    /// well-known AppId, or by publisher "Microsoft" + name "System" (symbol packages
    /// synthesized without an AppId). ISV apps that happen to be named "System" do NOT
    /// match (they must keep source-compiling and failing LOUD if broken).
    /// </summary>
    public static bool IsPlatformSymbolOnlySystemApp(Guid appId, string publisher, string name)
        => appId == PlatformSystemAppId
           || (string.Equals(publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
               && string.Equals(name, "System", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Report returned by <see cref="CheckPlatformApps"/>. Each issue entry is a symbol-only
    /// (non-R2R) platform app found in the cache that should be an R2R runtime package.
    /// </summary>
    public sealed record PlatformAppsReport(
        string Version,
        IReadOnlyList<(string Name, string AppVersion, string AppPath)> Issues,
        IReadOnlyList<string> SearchedDirs)
    {
        public bool Ok => Issues.Count == 0;

        /// <summary>
        /// One loud, self-contained message: names every symbol-only platform app + its path,
        /// the exact manual download command, and the one-command auto-resolve.
        /// </summary>
        public string ToDetailedMessage()
        {
            var lines = new List<string>
            {
                "BC runtime apps are not available as R2R packages — only symbol/dev packages were found.",
                "  Symbol-only packages cannot execute at runtime (their procedure bodies are external/native).",
                "",
                "  Apps found as symbol-only (provisioning gap):",
            };
            foreach (var (name, ver, path) in Issues)
                lines.Add($"    'Microsoft {name}' v{ver}  →  {path}");
            lines.Add("");
            lines.Add("  Resolve it ONE of these ways:");
            lines.Add("");
            lines.Add("  (a) One command (recommended):");
            lines.Add("        al-runner provision");
            lines.Add("      or re-run with --auto-provision.");
            lines.Add("");
            lines.Add("  (b) Force-download Microsoft platform apps only:");
            // Use the FIRST missing app's own real version — not a truncation of Version
            // (the engine version), which can be a different minor and would 404 against
            // the artifact CDN (it needs a FULL artifact version, e.g. 28.2.50931.52786).
            var suggestVer = Issues.Count > 0 ? Issues[0].AppVersion : "<full-version, e.g. 28.2.50931.52786>";
            lines.Add($"        al-runner provision --platform-apps --bc-version {suggestVer}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ── Bundle .alpackages discovery (issue #1678) ────────────────────────────
    // The startup gate that decides whether to fire --auto-provision (or fail loud
    // without it) used to scan ONLY the home-rooted default package caches
    // (~/.bcartifacts.cache, ~/.local/share/al-runner/{symbols,artifacts}) — never the
    // target bundles' own `.alpackages`. That is exactly where every standard AL project's
    // symbol download lives, so for the ordinary shape (symbol-only Microsoft platform
    // apps vendored in the project) the gate saw an EMPTY set, reported "Ok" vacuously, and
    // neither the loud failure nor the --auto-provision download ever fired — the run
    // limped all the way to a cryptic NavNCLMissingMethodException deep in dispatch. This
    // helper is the single source of truth for the bundle-rooted half of that scan, shared
    // by the startup gate, the `provision` subcommand, and any other caller that needs to
    // reason about what a bundle's own package cache actually vendors.

    /// <summary>
    /// Every `.alpackages` directory nested under any of <paramref name="bundlePaths"/>
    /// (recursively — a bundle can be a parent of many suites, each with its own
    /// `.alpackages`). Nonexistent paths are skipped; the result has no duplicates.
    /// Pure filesystem scan — no network, no manifest parsing.
    /// </summary>
    public static IReadOnlyList<string> CollectBundleAlpackagesDirs(IEnumerable<string> bundlePaths)
        => CollectBundleAlpackagesDirs(bundlePaths, out _);

    /// <inheritdoc cref="CollectBundleAlpackagesDirs(IEnumerable{string})"/>
    /// <param name="inaccessiblePaths">
    /// Every directory under a bundle whose contents could not be read. Reported rather than
    /// swallowed (issue #2206): an unreadable directory could be hiding an `.alpackages` the
    /// user expects to be found, and staying silent about it turns a permissions problem into
    /// a mysterious missing-dependency error much later in the run. Feed this to
    /// <see cref="FormatInaccessibleScanWarning"/> to produce the message.
    /// </param>
    public static IReadOnlyList<string> CollectBundleAlpackagesDirs(
        IEnumerable<string> bundlePaths, out IReadOnlyList<string> inaccessiblePaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var denied = new List<string>();
        foreach (var bundle in bundlePaths)
        {
            if (string.IsNullOrEmpty(bundle) || !Directory.Exists(bundle)) continue;
            // SafeDirectoryScan, not Directory.EnumerateDirectories(..., AllDirectories):
            // the latter is lazy, so the try/catch that used to sit around it guarded only
            // the enumerator's CONSTRUCTION and never its iteration — an unreadable
            // subdirectory anywhere under `bundle` escaped to Main and killed the process
            // with exit 134 (issue #2206). It also gives no way to report what was skipped.
            var found = AlRunner.Infrastructure.SafeDirectoryScan.Directories(
                bundle, ".alpackages", out var bundleDenied);
            foreach (var dir in found)
                if (seen.Add(dir))
                    result.Add(dir);
            foreach (var d in bundleDenied)
                denied.Add(d);
        }
        inaccessiblePaths = denied;
        return result;
    }

    /// <summary>
    /// The warning shown when the `.alpackages` scan could not read some directories, or
    /// <c>null</c> when it read everything.
    ///
    /// <para>Warning rather than staying silent is a measured choice (issue #2206). On the
    /// trees people actually point the runner at, this never fires: a real AL repository
    /// checkout (94,740 directories) and a whole workspace root (307,833 directories) both
    /// contain ZERO unreadable directories. The trees where it does fire are exactly the ones
    /// where an unreadable directory is the explanation the reader needs — <c>/tmp</c>, with
    /// its five <c>systemd-private-*</c> directories, is the case reported in the issue.
    /// So the warning costs nothing in the common case and supplies the missing diagnostic in
    /// the uncommon one; silence would instead convert a permissions problem into a
    /// missing-dependency mystery later in the run.</para>
    /// </summary>
    /// <param name="inaccessiblePaths">Paths reported by <see cref="CollectBundleAlpackagesDirs(IEnumerable{string}, out IReadOnlyList{string})"/>.</param>
    /// <param name="maxListed">
    /// How many paths to name before summarising the rest. Bounded so a tree with hundreds of
    /// unreadable directories cannot bury the run's real output; the remainder is counted
    /// rather than silently dropped.
    /// </param>
    public static string? FormatInaccessibleScanWarning(
        IReadOnlyList<string> inaccessiblePaths, int maxListed = 5)
    {
        if (inaccessiblePaths == null || inaccessiblePaths.Count == 0) return null;

        var lines = new List<string>
        {
            $"[warn] skipped {inaccessiblePaths.Count} unreadable director{(inaccessiblePaths.Count == 1 ? "y" : "ies")} while searching for `.alpackages`:",
        };
        foreach (var p in inaccessiblePaths.Take(maxListed))
            lines.Add($"         {p}");
        if (inaccessiblePaths.Count > maxListed)
            lines.Add($"         … and {inaccessiblePaths.Count - maxListed} more");
        lines.Add("       If a package cache you expected the runner to find lives under one of these,");
        lines.Add("       fix its permissions or point the runner directly at the app directory.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Scan <paramref name="packageCacheDirs"/> for known Microsoft platform runtime apps
    /// (System Application, Base Application, Business Foundation). If any are found as
    /// symbol-only (non-R2R) packages, returns a report listing them. Returns an Ok report
    /// when the apps are absent from the cache (they'll be served by service-tier DLLs) or
    /// when all found apps are R2R.
    /// </summary>
    public static PlatformAppsReport CheckPlatformApps(
        string version,
        IReadOnlyList<string> packageCacheDirs)
    {
        var issues = new List<(string Name, string AppVersion, string AppPath)>();

        foreach (var platformAppName in KnownPlatformRuntimeApps)
        {
            // Collect all instances of this platform app across all cache dirs.
            var found = new List<(string AppPath, string AppVersion, bool IsR2R)>();
            foreach (var dir in packageCacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var appFile in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app"))
                {
                    var m = AlRunner.AppLoader.ReadManifest(appFile);
                    if (m == null) continue;
                    if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(m.Name, platformAppName, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add((appFile, m.Version.ToString(), AlRunner.AppLoader.IsR2R(appFile)));
                }
            }

            // Issue: at least one instance found but NONE is R2R.
            if (found.Count > 0 && !found.Any(f => f.IsR2R))
            {
                var best = found.OrderByDescending(f => f.AppVersion).First();
                issues.Add((platformAppName, best.AppVersion, best.AppPath));
            }
        }

        return new PlatformAppsReport(version, issues, packageCacheDirs);
    }

    /// <summary>
    /// Derives the BC major.minor a symbol-only platform app already in the cache would
    /// suggest for auto-provisioning. Carried in <paramref name="report"/>.Issues[0]
    /// .AppVersion when one exists; falls back to <paramref name="fallbackVersion"/>'s
    /// major.minor otherwise. Pure — does no I/O.
    ///
    /// Issue #2077: this used to be the value callers actually downloaded — "the engine is
    /// version-agnostic w.r.t. the R2R apps it dispatches to, so download whatever the
    /// on-disk symbol-only app needs" sounds reasonable until the app already in the cache
    /// is simply a STALE artifact (a project's committed `.alpackages`, an old warm run) at
    /// a DIFFERENT minor than the BC version the caller explicitly selected — then this
    /// silently redirected the whole provisioning pass to that stale minor instead. Once a
    /// BC version has been selected, provisioning MUST target that version — see
    /// <see cref="ResolveProvisionMajorMinor"/>, the function callers now use for the
    /// actual decision. This one is kept only as a pure cache-inspection signal, e.g. to
    /// build the loud mismatch note <see cref="BuildProvisionVersionSkewNote"/> emits when
    /// the cache disagrees with the selection.
    /// </summary>
    public static string DeriveProvisionMajorMinor(PlatformAppsReport report, string fallbackVersion)
    {
        var source = report.Issues.Count > 0 ? report.Issues[0].AppVersion : fallbackVersion;
        return MajorMinorOf(source);
    }

    /// <summary>
    /// The BC major.minor to actually auto-provision platform apps/the test toolkit for:
    /// always <paramref name="selectedVersion"/>'s own major.minor (issue #2077 — once a BC
    /// version is selected, provisioning targets THAT version, never one derived from cache
    /// contents, a symbol-only closure, or a project's vendored `.alpackages`). Pure — does
    /// no I/O.
    /// </summary>
    public static string ResolveProvisionMajorMinor(string selectedVersion) => MajorMinorOf(selectedVersion);

    /// <summary>Shared major.minor extraction used by the Derive*/Resolve* helpers above.</summary>
    private static string MajorMinorOf(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? string.Join(".", parts.Take(2)) : version;
    }

    /// <summary>
    /// Issue #2077 acceptance criterion: "if the runner ever compiles against platform apps
    /// whose build differs from the selected engine build, it says so explicitly." Returns
    /// a loud one-line note when <paramref name="cacheDerivedMajorMinor"/> — what the
    /// package cache alone would have suggested (<see cref="DeriveProvisionMajorMinor"/> /
    /// <see cref="DerivePresentPlatformMajorMinor"/>) — disagrees with
    /// <paramref name="selectedMajorMinor"/> (what <see cref="ResolveProvisionMajorMinor"/>
    /// actually provisions). Null when they agree, so callers can
    /// `if (note != null) Console.Error.WriteLine(note);` unconditionally. Pure.
    /// </summary>
    public static string? BuildProvisionVersionSkewNote(
        string selectedMajorMinor, string cacheDerivedMajorMinor, string cacheSource)
    {
        if (string.Equals(selectedMajorMinor, cacheDerivedMajorMinor, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"[provision] note: {cacheSource} suggest(s) BC {cacheDerivedMajorMinor}.x, which differs from " +
               $"the selected BC {selectedMajorMinor}.x — provisioning platform R2R apps for the SELECTED " +
               $"version, not the cache's.";
    }

    /// <summary>
    /// Builds the loud, actionable warning message for a known Microsoft platform app
    /// that was found in the package cache as a symbol-only (non-R2R) package. Emitted
    /// by DependencyLoader when it detects this condition to convert the otherwise cryptic
    /// "EMIT-ZERO" error into a provisioning-gap explanation.
    /// </summary>
    public static string BuildPlatformAppMissingR2RMessage(
        string publisher, string appName, string appVersion, string symbolOnlyPath, string bcVersion)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"[provision-gap] '{publisher} {appName}' v{appVersion} is not available as an R2R runtime package.",
            $"  Found:  {symbolOnlyPath}",
            $"  Status: symbol/dev package only — cannot execute at runtime (procedure bodies are external/native).",
            $"  Engine version: {bcVersion} (the app's own version, {appVersion}, may be a different minor —",
            $"  the engine is version-agnostic w.r.t. the R2R apps it dispatches to at runtime).",
            $"  The runner will use service-tier DLL dispatch as a fallback.",
            $"",
            $"  Fix: run ONE of:",
            $"    al-runner provision  (or re-run with --auto-provision)",
            // Suggest the APP's own version, not bcVersion (the engine's) — the engine is
            // version-agnostic w.r.t. the R2R apps it dispatches to, so these can differ
            // (e.g. engine 28.1 running 28.2 R2R apps); using bcVersion here would 404.
            $"    al-runner provision --platform-apps --bc-version {appVersion}",
        });
    }

    // ── Test-toolkit presence check ───────────────────────────────────────────
    // Microsoft ships the test-toolkit apps (Business Foundation Test Libraries,
    // Application Test Library, System Application Test Library, Test Runner, Library
    // Assert, Library Variable Storage, Tests-TestLibraries, Permissions Mock, …) as a
    // SEPARATE artifact set from the w1 platform apps (they live under the `platform`
    // artifact's Applications/<area>/Test/ tree, not w1/Extensions). A clean cache that
    // only has the platform apps still fails to compile any test bundle that depends on
    // ALTestRunner/Library Assert/etc. Detect that gap here so --auto-provision can close
    // it too, instead of leaving the "re-run with --auto-provision" message from a
    // different check to lie about what it actually does.
    //
    // We only need to detect the presence of ONE well-known test-toolkit app to know the
    // whole set was provisioned together (ArtifactDownloader.TestApps always downloads the
    // full set in one shot) — checking for all of them would just be redundant I/O.
    // The definitive marker that the Microsoft test toolkit is provisioned. Chosen
    // deliberately: it is the foundational test lib every BC test bundle transitively
    // depends on (chain: Tests-TestLibraries → Application Test Library → Business
    // Foundation Test Libraries) AND it is provided ONLY by the downloaded test-apps
    // set — a project's own .alpackages commonly vendors "Application Test Library" but
    // NOT this one. Using a looser OR-match (accepting Application Test Library) falsely
    // reports the toolkit "present" for a cache that vendors App Test Library yet still
    // lacks Business Foundation Test Libraries, so the auto-provision download never fires
    // and the test bundle then fails to compile — exactly the gap this check exists to catch.
    public const string TestToolkitSentinelApp = "Business Foundation Test Libraries";

    /// <summary>
    /// True if the Microsoft test toolkit is provisioned in <paramref name="packageCacheDirs"/>,
    /// detected via <see cref="TestToolkitSentinelApp"/> (a Microsoft-published .app of that
    /// name). Missing/nonexistent dirs are skipped. Pure filesystem scan — no network.
    ///
    /// Issue #2003: <paramref name="versionFloors"/> is the manifest-declared minimum
    /// version, if any, for <see cref="TestToolkitSentinelApp"/> (see
    /// <see cref="DetermineVersionFloors"/>). A sentinel app found below its floor does NOT
    /// count as present — presence alone used to be sufficient, which let a warm-but-stale
    /// toolkit satisfy this check and get silently reused past the version the bundle's own
    /// app.json actually requires. Null/empty (the default) preserves the old presence-only
    /// behavior — a bundle whose manifests declare no floor is unaffected (AC #4).
    /// </summary>
    public static bool TestToolkitPresent(
        IReadOnlyList<string> packageCacheDirs,
        IReadOnlyDictionary<string, Version>? versionFloors = null)
    {
        var floor = versionFloors != null && versionFloors.TryGetValue(TestToolkitSentinelApp, out var f) ? f : null;
        foreach (var dir in packageCacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var appFile in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app"))
            {
                var m = AlRunner.AppLoader.ReadManifest(appFile);
                if (m == null) continue;
                if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(TestToolkitSentinelApp, m.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (floor != null && m.Version < floor) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The BC major.minor a Microsoft "Base Application"/"System Application" .app already
    /// present in <paramref name="packageCacheDirs"/> would suggest for auto-provisioning.
    /// Falls back to <paramref name="fallbackVersion"/>'s major.minor when no such app is
    /// found. Pure filesystem scan — no network.
    ///
    /// Issue #2077: this used to be the value callers actually downloaded whenever no
    /// symbol-only-R2R issue existed — "the platform apps already in the cache are the most
    /// reliable signal of which minor this project targets" sounds reasonable until the
    /// apps already in the cache are simply a committed `.alpackages` symbol closure at a
    /// DIFFERENT minor than the BC version the caller explicitly selected (e.g. `--bc-
    /// version 28.4` with a project-vendored 28.1 closure) — then this silently redirected
    /// the whole provisioning pass to that unrelated minor instead, and the run went on to
    /// compile against it while reporting `[bc] selected BC 28.4...`. Once a BC version has
    /// been selected, provisioning MUST target that version — see
    /// <see cref="ResolveProvisionMajorMinor"/>, the function callers now use for the
    /// actual decision. This one is kept only as a pure cache-inspection signal, e.g. to
    /// build the loud mismatch note <see cref="BuildProvisionVersionSkewNote"/> emits when
    /// the cache disagrees with the selection.
    /// </summary>
    public static string DerivePresentPlatformMajorMinor(
        IReadOnlyList<string> packageCacheDirs, string fallbackVersion)
    {
        foreach (var dir in packageCacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var appFile in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app"))
            {
                var m = AlRunner.AppLoader.ReadManifest(appFile);
                if (m == null) continue;
                if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(m.Name, "Base Application", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(m.Name, "System Application", StringComparison.OrdinalIgnoreCase))
                    continue;
                return MajorMinorOf(m.Version.ToString());
            }
        }
        return MajorMinorOf(fallbackVersion);
    }

    // ── Auto-provision download destination (issue #1653) ────────────────────
    // `--auto-provision` used to download platform R2R apps + the MS test toolkit into
    // whichever `--package-cache` dir the caller passed first — i.e. into the *project's*
    // .alpackages (up to ~135 MB / 112 files). That directory is the user's; the runner
    // must never write into it unasked. These two helpers are the single source of truth
    // for the correct destination: the runner-owned artifact cache, exactly mirroring the
    // layout the standalone `al-runner provision` step already uses for the test toolkit
    // (`<artifacts-root>/<version>/test-apps`). Pure path composition — no I/O, so callers
    // can create the dir, or add it as a search root, as needed.

    /// <summary>
    /// Runner-owned directory to download Microsoft platform R2R runtime apps (System
    /// Application, Base Application, Business Foundation) into. Sibling of
    /// <see cref="TestAppsDirFor"/> under the same per-version artifact root.
    ///
    /// Issue #2236: keyed by <paramref name="country"/> (default = <see
    /// cref="BcArtifacts.SelectedCountry"/>, "w1" unless <c>--country</c> was passed) so a
    /// w1 set and a country-localized set for the SAME BC build never collide — a country
    /// artifact's Base Application is a genuinely different file, not a superset of w1's
    /// (see ArtifactDownloader.PlatformApps). "w1" keeps the pre-#2236 bare directory name
    /// (no migration needed for existing caches / test fixtures); any other country gets
    /// its own "platform-apps-&lt;country&gt;" sibling, so provisioning both leaves both
    /// usable side by side instead of one silently overwriting the other.
    /// </summary>
    public static string PlatformAppsDirFor(string artifactsRootDir, string fullVersion, string? country = null)
    {
        var c = NormalizeCountry(country);
        return c == "w1"
            ? Path.Combine(artifactsRootDir, fullVersion, "platform-apps")
            : Path.Combine(artifactsRootDir, fullVersion, $"platform-apps-{c}");
    }

    /// <summary>null → <see cref="BcArtifacts.SelectedCountry"/> (the process-wide default);
    /// otherwise trim + lowercase the explicit value the same way SelectedCountry does.</summary>
    private static string NormalizeCountry(string? country)
        => string.IsNullOrWhiteSpace(country) ? BcArtifacts.SelectedCountry : country.Trim().ToLowerInvariant();

    /// <summary>
    /// Runner-owned directory to download the Microsoft test toolkit (Business Foundation
    /// Test Libraries, Application Test Library, Library Assert, Test Runner, …) into.
    /// </summary>
    public static string TestAppsDirFor(string artifactsRootDir, string fullVersion)
        => Path.Combine(artifactsRootDir, fullVersion, "test-apps");

    /// <summary>
    /// Every version directory directly under <paramref name="artifactsRootDir"/> whose
    /// name is exactly <paramref name="majorMinorPrefix"/> or starts with
    /// <c>"&lt;majorMinorPrefix&gt;."</c> — i.e. every provisioned patch of the same
    /// major.minor line — ordered by descending <see cref="Version"/> (newest first,
    /// matching the order the warm-reuse scan in Program.cs's FindWarmProvisionedVersion
    /// already uses). Pure directory-name scan; a missing root yields no candidates
    /// rather than throwing.
    /// </summary>
    public static IReadOnlyList<string> EnumerateVersionDirNames(
        string artifactsRootDir, string majorMinorPrefix)
    {
        if (!Directory.Exists(artifactsRootDir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(artifactsRootDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n)
                && (n == majorMinorPrefix || n!.StartsWith(majorMinorPrefix + ".", StringComparison.Ordinal)))
            .Select(n => (Name: n!, Ver: Version.TryParse(n, out var v) ? v : null))
            .Where(t => t.Ver != null)
            .OrderByDescending(t => t.Ver)
            .Select(t => t.Name)
            .ToList();
    }

    /// <summary>
    /// Every runner-owned platform-apps/test-apps directory that ACTUALLY EXISTS ON DISK
    /// for any patch version sharing <paramref name="majorMinorPrefix"/> with the selected
    /// engine version — not just the engine's own exact patch (issue #2234).
    ///
    /// Issue #2226 (a separate, still-open defect) can make `provision`'s platform-app and
    /// test-app sub-steps land under DIFFERENT patch directories of the same major.minor —
    /// one resolved from the engine's own exact build, the other from the CDN's "latest"
    /// for the major.minor. Need detection used to build its search set from the SELECTED
    /// engine version alone (<see cref="PlatformAppsDirFor"/>/<see cref="TestAppsDirFor"/>
    /// called with just that one version), so an app provisioned under a sibling patch
    /// directory read as absent even though it was present and usable — the run path found
    /// it only as an incidental side effect of the reuse scan (FindWarmProvisionedVersion)
    /// that runs solely inside the auto-provision download branch, so `--no-auto-provision`
    /// never benefited from it and reported the identical "missing" diagnostic forever,
    /// even immediately after `provision` had just completed successfully.
    ///
    /// This is the single source of truth both the run path and need detection now share,
    /// so they can no longer independently drift onto different search sets. Pure
    /// filesystem existence scan — no network — and it can only ADD directories that exist
    /// on disk, never remove or fabricate one.
    /// </summary>
    public static IReadOnlyList<string> CollectRunnerOwnedProvisionDirs(
        string artifactsRootDir, string majorMinorPrefix)
    {
        var dirs = new List<string>();
        foreach (var name in EnumerateVersionDirNames(artifactsRootDir, majorMinorPrefix))
        {
            var platformDir = PlatformAppsDirFor(artifactsRootDir, name);
            if (Directory.Exists(platformDir)) dirs.Add(platformDir);
            var testDir = TestAppsDirFor(artifactsRootDir, name);
            if (Directory.Exists(testDir)) dirs.Add(testDir);
        }
        return dirs;
    }

    // ── Issue #1996: manifest-driven need detection ──────────────────────────
    // CheckPlatformApps / TestToolkitPresent above are REACTIVE: they can only report a
    // gap for an app that is already PRESENT (as symbol-only) in the cache. An empty
    // cache — no .alpackages at all, or a --package-cache dir that doesn't exist yet —
    // therefore reads as vacuously "Ok": nothing was found, so nothing looks broken, so
    // --auto-provision never fires and the run limps to a cryptic "Missing:" error deep
    // in dependency resolution instead. The bundle's OWN app.json manifests are an
    // independent source of truth for what's actually required, regardless of what
    // currently exists on disk — these functions consult THAT instead.
    //
    // Two curated, ATOMIC download sets exist (see ArtifactDownloader.PlatformApps /
    // .TestApps): a project either needs the whole platform-apps set or none of it (same
    // for test-apps) — there is no per-app partial download. So a DOWNLOAD, once triggered,
    // always fetches the full set. But "is it already satisfied" must NOT require every
    // member of that set to be individually present on disk: System/Base Application and
    // Business Foundation have a service-tier DLL dispatch fallback (the runner runs their
    // codeunits even with no .app vendored at all — see KnownPlatformRuntimeApps' doc
    // comment; only PRESENT-but-symbol-only is a gap for them, unchanged, via
    // CheckPlatformApps). Application Test Library has NO such fallback (see
    // ArtifactDownloader.PlatformApps' comment on it) — a bundle that needs it is
    // unresolvable without a real .app in cache. So the "must be literally present" check
    // is scoped to KnownNoFallbackPlatformApps, not the whole downloadable set — requiring
    // the whole set would regress nearly every bundle in the corpus (virtually all of them
    // carry implicit `application`/`platform` roots) into a spurious "needs download".

    /// <summary>
    /// Real Microsoft app names whose absence from the package cache is ALWAYS a genuine
    /// gap — they have no service-tier DLL dispatch fallback the way System/Base
    /// Application and Business Foundation do, so a bundle needing one is unresolvable
    /// without a real (R2R or source-compilable) .app on disk. Application Test Library
    /// ships in the w1 `platform-apps` set (see ArtifactDownloader.PlatformApps), not the
    /// `test-apps` set — the specific miss issue #1996 is about.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownNoFallbackPlatformApps = new[]
    {
        "Application Test Library",
    };

    /// <summary>
    /// The Microsoft app names <see cref="AlRunner.Provisioning.ArtifactDownloader.PlatformApps"/>
    /// actually supplies — the four w1 <c>Extensions/</c> apps, Application Test Library,
    /// and the platform artifact's <c>System.app</c>. This is a description of the DOWNLOAD
    /// SET's contents, not a policy list: it exists so a manifest need can only ever be
    /// declared for something the download can satisfy (a Microsoft app outside it — an
    /// unrelated MS extension, say — must never become an unsatisfiable requirement).
    /// </summary>
    public static readonly IReadOnlyList<string> PlatformAppSetContents = new[]
    {
        "Application",
        "System",
        "System Application",
        "Base Application",
        "Business Foundation",
        "Application Test Library",
    };

    /// <summary>
    /// Which apps a manifest need may be declared for — the whole contents of the curated
    /// platform-apps download set (issue #2205), asked one app at a time against what the
    /// manifests actually name.
    ///
    /// It used to be <see cref="KnownNoFallbackPlatformApps"/> alone, on the premise that
    /// System/Base Application and Business Foundation always have a service-tier DLL
    /// dispatch fallback, so only PRESENT-but-symbol-only could ever be a gap for them and
    /// requiring the whole set would regress every corpus bundle (they all carry implicit
    /// `application`/`platform` roots) into a spurious "needs download".
    ///
    /// The premise is false on a cold cache. The DLL fallback serves RUNTIME DISPATCH; it
    /// does not supply COMPILE-TIME SYMBOLS. With engine-only artifacts on disk an ordinary
    /// AL app — `"dependencies": []` plus `application`/`platform`, the default shape of
    /// virtually every real extension — never compiles, so there is no runtime for the
    /// fallback to serve: the run ended in EMIT-EXCLUDED preceded by two unframed
    /// `[deps] dependency not found in cache, skipping` lines. CI never saw it because CI
    /// machines always already have platform-apps/ on disk.
    ///
    /// The spurious-download worry the old premise was protecting against does not follow
    /// from broadening the targets, because the decision is not "is this app named in a
    /// list" — it is <see cref="FindMissingPlatformApps"/>, "is this app ABSENT from every
    /// searched cache", asked only of the apps the manifests named. A warm machine has them,
    /// so a warm bundle still decides "no download"; a cold one does not, which is the whole
    /// point. And a bundle that names, say, no Business Foundation is never asked for it.
    /// </summary>
    private static readonly IReadOnlyList<string> PlatformNeedTargets = PlatformAppSetContents;

    /// <summary>
    /// True iff <paramref name="appName"/> itself is in <paramref name="targets"/>, or
    /// reaches a member of it by following <paramref name="edges"/> through any number of
    /// hops. Pure graph BFS — the general closure-walk mechanism issue #2087 asked for,
    /// exposed separately from <see cref="DetermineManifestNeeds"/> so the WALK itself (not
    /// just the apps whose manifests <see cref="ScanDependencyEdges"/> happens to find on
    /// this machine) can be proven against synthetic data. Cycle-safe — a real manifest
    /// graph with a loop terminates instead of hanging — and case-insensitive on names,
    /// matching every other Microsoft-app-name comparison in this file.
    /// </summary>
    public static bool ReachesAnyOf(
        string appName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> edges,
        IReadOnlyList<string> targets)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { appName };
        var queue = new Queue<string>();
        queue.Enqueue(appName);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (targets.Any(t => string.Equals(t, current, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!edges.TryGetValue(current, out var deps)) continue;
            foreach (var dep in deps)
                if (visited.Add(dep))
                    queue.Enqueue(dep);
        }
        return false;
    }

    /// <summary>
    /// The dependency edges read out of the Microsoft `.app` packages found on disk, plus
    /// every package the scan could NOT read. Both halves matter: an unreadable package is
    /// an app whose edges are unknown, and silently folding that into "declares nothing"
    /// is precisely the quiet wrong answer .claude/rules/loud-failures.md exists to stop —
    /// it reads downstream as "this app needs no provisioning".
    /// </summary>
    public sealed record DependencyEdgeScan(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Edges,
        IReadOnlyList<string> UnreadablePackages);

    /// <summary>
    /// Reads the REAL direct dependency edges among Microsoft apps out of the `.app`
    /// packages present in <paramref name="searchDirs"/> — each app's own
    /// <c>NavxManifest.xml</c> &lt;Dependencies&gt; block, via
    /// <see cref="AlRunner.AppLoader.ReadManifest"/> (which is memo- and disk-index-cached,
    /// so a warm repeat scan is a dictionary lookup rather than a re-parse).
    ///
    /// Issue #2103: this replaces a hand-transcribed edge table. A hand table is
    /// version-blind by construction, and Microsoft moves apps between packages across BC
    /// releases — measured across the versions in <c>.github/bc-versions.txt</c>:
    /// <c>Tests-TestLibraries</c> depends on <c>Application Test Library</c> on BC 28.x, and
    /// does NOT on BC 27.x (where that app is absent from the w1 artifact entirely, and the
    /// edge is <c>Library Variable Storage</c> + <c>Business Foundation Test Libraries</c>
    /// instead). A table recording one of those shapes is silently wrong for the other, and
    /// the resulting failure looks like a runner capability gap rather than a stale table.
    ///
    /// Only Microsoft-published packages become edge SOURCES, and only Microsoft-published
    /// dependencies become edge TARGETS: the graph is keyed by app NAME (matching every
    /// other Microsoft-app comparison in this file) and the walk's targets
    /// (<see cref="KnownNoFallbackPlatformApps"/>) are Microsoft apps, so admitting
    /// third-party names could only introduce name collisions, never a real path.
    ///
    /// Each directory is walked RECURSIVELY, matching
    /// <see cref="NoFallbackPlatformAppsPresent"/> and <see cref="TestToolkitPresent"/> —
    /// those answer "is this app present" over the same dirs, and the two views must not
    /// disagree about which packages exist.
    ///
    /// An app that declares NO dependencies is recorded with an EMPTY list rather than
    /// omitted — "scanned, declares nothing" is a fact, and must not be indistinguishable
    /// from "never looked at". When the same app name appears in more than one directory,
    /// the FIRST directory in <paramref name="searchDirs"/> wins, matching the search-order
    /// precedence the rest of the package-cache lookups use.
    /// </summary>
    /// <summary>&quot;No edges known&quot; — the honest default when nothing has been
    /// downloaded yet, in place of the version-blind hand-written table issue #2103
    /// removed.</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyDependencyEdges =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public static DependencyEdgeScan ScanDependencyEdges(IEnumerable<string> searchDirs)
    {
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string full;
            try { full = Path.GetFullPath(dir); } catch { continue; }
            if (!seenDirs.Add(full)) continue;
            // A directory that simply does not exist is NOT a failure: the search set
            // routinely names dirs that only appear once something is provisioned. An
            // unreadable FILE inside an existing dir is a different thing and is reported.
            if (!Directory.Exists(full)) continue;

            List<string> files;
            try
            {
                // SearchOption.AllDirectories, matching NoFallbackPlatformAppsPresent and
                // TestToolkitPresent above: those two decide "is this app PRESENT" over the
                // same search dirs, and a scan that saw a narrower set could report an app's
                // edges as unknown while its sibling reported the app itself as present —
                // two answers about the same state, from the same dirs, under different
                // rules. ReadManifest is memo/disk-index cached and the siblings already
                // walk these trees, so the extra walk is a cache hit, not a re-parse.
                files = AlRunner.Infrastructure.SafeDirectoryScan.Files(full, "*.app")
                    .OrderBy(f => f, StringComparer.Ordinal).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                AlRunner.AppManifest? manifest;
                try { manifest = AlRunner.AppLoader.ReadManifest(file); }
                catch (Exception) { manifest = null; }

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name))
                {
                    unreadable.Add(file);
                    continue;
                }
                if (!string.Equals(manifest.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (edges.ContainsKey(manifest.Name)) continue;

                var deps = new List<string>();
                foreach (var d in manifest.Dependencies)
                {
                    if (!string.Equals(d.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(d.Name)) continue;
                    if (!deps.Any(x => string.Equals(x, d.Name, StringComparison.OrdinalIgnoreCase)))
                        deps.Add(d.Name);
                }
                edges[manifest.Name] = deps;
            }
        }

        return new DependencyEdgeScan(edges, unreadable);
    }

    /// <summary>
    /// Real Microsoft app names supplied by the curated `test-apps` download (platform
    /// artifact's Applications/&lt;area&gt;/Test + TestFramework trees — see
    /// ArtifactDownloader.TestApps). Deliberately a closed allowlist, NOT "any Microsoft
    /// app whose name contains Test" — an arbitrary Microsoft application extension (e.g.
    /// a first-party ISV-style app) must never trigger a test-toolkit download it doesn't
    /// need and the curated set can't satisfy anyway (issue #1996 acceptance criterion:
    /// apps outside the known test-framework roots must not trigger test-apps provisioning).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownTestFrameworkAppNames = new[]
    {
        "Business Foundation Test Libraries",
        "System Application Test Library",
        "Tests-TestLibraries",
        "Library Assert",
        "Library Variable Storage",
        "Test Runner",
        "Any",
        "Permissions Mock",
    };

    /// <summary>
    /// Every Microsoft app name the w1 (worldwide) artifact download commands
    /// (`al-runner provision` / `--auto-provision`, and their `--platform-apps` /
    /// `--test-apps` narrower forms) can ever actually fetch: <see
    /// cref="KnownPlatformRuntimeApps"/> (System Application, Base Application, Business
    /// Foundation) + the two curated w1/Extensions apps ArtifactDownloader.PlatformApps
    /// also downloads by name ("Application", "Application Test Library") + the platform
    /// symbol app "System" (ArtifactDownloader.SystemApp) + <see
    /// cref="KnownTestFrameworkAppNames"/>.
    ///
    /// Issue #2236: MissingDependencyException.ToDetailedMessage uses this to decide
    /// whether "re-run with --auto-provision" is actually true advice for a given
    /// Microsoft-publisher dependency, or whether it is dead advice a user could follow
    /// forever without result — a country-localization app such as "IRS Forms" is
    /// Microsoft-published but is not in this list, because it ships only in a
    /// country-specific artifact channel (see ArtifactDownloader.PlatformApps'
    /// country-aware download), not the w1 one these two commands always fetch.
    /// </summary>
    public static bool IsKnownW1DownloadableAppName(string appName)
        => IsKnownPlatformRuntimeApp(appName)
           || string.Equals(appName, "Application", StringComparison.OrdinalIgnoreCase)
           || string.Equals(appName, "Application Test Library", StringComparison.OrdinalIgnoreCase)
           || string.Equals(appName, "System", StringComparison.OrdinalIgnoreCase)
           || KnownTestFrameworkAppNames.Any(n => string.Equals(n, appName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Manifest-derived provisioning need, independent of what's currently on disk.
    /// <paramref name="RequiredPlatformApps"/> names the individual apps out of the curated
    /// platform-apps set that these manifests actually require, so callers can check
    /// presence of exactly those (and name the absent ones) instead of a fixed list —
    /// see <see cref="FindMissingPlatformApps"/>.
    /// </summary>
    public sealed record ManifestNeeds(
        bool NeedsPlatformApps, bool NeedsTestApps, IReadOnlyList<string> RequiredPlatformApps);

    /// <summary>
    /// Classifies a bundle's unioned dependency roots (see Program.ReadBundleDependencyRoots)
    /// into which curated download set(s) — if any — the bundle needs. Pure — does no I/O.
    /// </summary>
    /// <param name="roots">The bundle's own unioned dependency roots.</param>
    /// <param name="dependencyEdges">
    /// Microsoft app dependency edges to walk via <see cref="ReachesAnyOf"/> when deciding
    /// whether a root transitively needs <see cref="KnownNoFallbackPlatformApps"/>. Supply
    /// them from <see cref="ScanDependencyEdges"/> — the real
    /// <c>NavxManifest.xml</c> &lt;Dependencies&gt; of whatever Microsoft packages are on
    /// disk (issue #2103); <see cref="DecideManifestProvisioning"/> does exactly that.
    /// Null (the default) means "no edges known", which degrades this to a DIRECT
    /// membership test rather than inventing a version-blind guess — the honest answer when
    /// nothing has been downloaded yet. Tests also pass synthetic graphs here to prove the
    /// WALK itself (issue #2087).
    /// </param>
    /// <param name="extraPlatformNeedCandidates">
    /// Issue #2236: additional app names to ask "do these manifests require this" for,
    /// beyond <see cref="PlatformNeedTargets"/> (the fixed w1-curated set). Pass null for
    /// an ordinary w1 run — widening the candidate set there would invent an unsatisfiable
    /// requirement for any Microsoft app outside the w1 download's actual contents (the
    /// bug MissingDependencyException.ToDetailedMessage's #2236 fix addresses on the
    /// message side; doing it here too would instead spend a real download attempt trying
    /// and still failing). Only the caller's own <c>--country</c> selection knows whether
    /// widening is safe: when a NON-w1 country is selected, ArtifactDownloader.PlatformApps
    /// fetches every Microsoft app under Extensions/, not just the curated five, so a
    /// direct Microsoft-publisher root the bundle declares (e.g. "IRS Forms") IS something
    /// that download can satisfy and belongs in the candidate set too.
    /// </param>
    public static ManifestNeeds DetermineManifestNeeds(
        IEnumerable<AlRunner.DependencyRef> roots,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? dependencyEdges = null,
        IReadOnlyList<string>? extraPlatformNeedCandidates = null)
    {
        var edges = dependencyEdges ?? EmptyDependencyEdges;
        var rootList = roots as IReadOnlyCollection<AlRunner.DependencyRef> ?? roots.ToList();
        bool needsTest = false;

        // Which apps out of the curated platform-apps set (plus, for a non-w1 country, any
        // extra candidate the caller supplied) do these manifests require? Asked per-app,
        // over whatever the manifests NAME (directly, or reach through recorded edges) —
        // never a fixed exemption list. Iterating in declared order keeps the result
        // deterministic for messages and tests; Distinct guards against a candidate
        // appearing in both lists (harmless either way, but avoids a duplicate entry in
        // RequiredPlatformApps/the eventual message).
        var candidates = extraPlatformNeedCandidates == null
            ? PlatformNeedTargets
            : PlatformNeedTargets.Concat(extraPlatformNeedCandidates)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var requiredPlatformApps = new List<string>();
        foreach (var candidate in candidates)
        {
            var single = new[] { candidate };
            foreach (var d in rootList)
            {
                if (!string.Equals(d.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ReachesAnyOf(d.Name, edges, single)) continue;
                requiredPlatformApps.Add(candidate);
                break;
            }
        }
        bool needsPlatform = requiredPlatformApps.Count > 0;

        foreach (var d in rootList)
        {
            if (!string.Equals(d.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            // Issue #2087: ONE closure walk replaces what used to be two separate checks —
            // a direct KnownNoFallbackPlatformApps membership test, and a second hardcoded
            // list of names known (by hand) to transitively reach one. ReachesAnyOf treats
            // "d.Name IS a no-fallback app" and "d.Name REACHES one through recorded edges"
            // as the same question, so a future multi-hop chain is caught the moment its own
            // edge is recorded — no new list, no per-shape entry.
            if (ReachesAnyOf(d.Name, edges, KnownNoFallbackPlatformApps))
            {
                // Confirmed via a live BC 28.1 platform-apps download (issue #1996): App
                // Test Library's OWN manifest transitively depends on the MS test toolkit
                // (Any, and from there Library Assert/Business Foundation Test Libraries)
                // — invisible to this pre-scan, which only reads the BUNDLE's app.json, not
                // a downloaded dependency's OWN manifest. Needing the no-fallback platform
                // app therefore always implies needing the test toolkit too.
                needsTest = true;
            }
            if (KnownTestFrameworkAppNames.Any(n => string.Equals(n, d.Name, StringComparison.OrdinalIgnoreCase)))
                needsTest = true;
        }
        return new ManifestNeeds(needsPlatform, needsTest, requiredPlatformApps);
    }

    // ── Issue #2003: manifest-driven version floors ──────────────────────────
    // DetermineManifestNeeds above answers "is this app needed at all". It says nothing
    // about WHICH version satisfies that need — a bundle's app.json can declare a
    // dependency at a specific minimum version (e.g. Application Test Library 28.1.0.0),
    // and a warm-provisioned set at the same major/minor but an OLDER patch used to satisfy
    // every presence check unconditionally. The failure wasn't loud: the apps resolved,
    // compilation proceeded, and the run failed later on whatever the missing symbol or
    // changed signature produced — pointing at the test code instead of the stale
    // provisioning. These helpers make the floor visible to the same presence checks that
    // already decide "is this app already provisioned", so a stale warm set stops looking
    // complete.

    /// <summary>
    /// The version floor (minimum acceptable version) each Microsoft dependency name
    /// declares across <paramref name="roots"/> — the SAME roots
    /// <see cref="DetermineManifestNeeds"/> reads. When multiple manifests (or bundles)
    /// declare different floors for the same app name, the HIGHER one wins: a looser
    /// dependency declared elsewhere can never relax what the strictest manifest requires.
    /// Case-insensitive on name. Non-Microsoft roots are ignored (floors are only meaningful
    /// for the curated platform-apps/test-apps sets, which are Microsoft-only). Pure — does
    /// no I/O. Returns an empty (never null) map when no root declares a Microsoft
    /// dependency, so callers can pass the result straight through without a null check —
    /// which is also exactly AC #4: a bundle whose manifests declare no floor gets an empty
    /// map, and every floor-aware lookup below then behaves identically to the old
    /// presence-only check.
    /// </summary>
    public static IReadOnlyDictionary<string, Version> DetermineVersionFloors(IEnumerable<AlRunner.DependencyRef> roots)
    {
        var floors = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in roots)
        {
            if (!string.Equals(d.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            if (!floors.TryGetValue(d.Name, out var existing) || d.Version > existing)
                floors[d.Name] = d.Version;
        }
        return floors;
    }

    /// <summary>
    /// <paramref name="versionFloors"/> minus every floor that <paramref name="provisionableVersion"/>
    /// could not possibly satisfy — a floor whose major.minor is ABOVE the BC version being
    /// provisioned.
    ///
    /// Issue #2003's floor rule was measured on a stale PATCH: 28.0 present, 28.1 declared,
    /// a newer build obtainable — so "found but too old" is a real gap a download fixes.
    /// A floor above the selected version is a different thing entirely and no download can
    /// ever clear it. The al-language corpus declares System Application / Base Application
    /// &gt;= 27.5.0.0 and is nonetheless run — green — on the BC 27.0 and 27.3 legs, because
    /// the runner tolerates that gap deliberately (<c>BcFloorGate</c> owns the case where it
    /// is genuinely incompatible). Counting it as "absent" once issue #2205 made these apps
    /// a real requirement would have turned both legs into "platform apps still missing
    /// after download", exit 2, on every cold run.
    ///
    /// An unparseable <paramref name="provisionableVersion"/> rules nothing out: every floor
    /// is kept, because "we cannot tell" must not read as "relax them all". Pure.
    /// </summary>
    public static IReadOnlyDictionary<string, Version> DropUnsatisfiableFloors(
        IReadOnlyDictionary<string, Version>? versionFloors, string? provisionableVersion)
    {
        var empty = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        if (versionFloors == null || versionFloors.Count == 0) return empty;
        if (string.IsNullOrWhiteSpace(provisionableVersion)) return versionFloors;

        var parts = provisionableVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return versionFloors;

        var kept = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, floor) in versionFloors)
        {
            var floorMinor = floor.Minor < 0 ? 0 : floor.Minor;
            if (floor.Major > major || (floor.Major == major && floorMinor > minor)) continue;
            kept[name] = floor;
        }
        return kept;
    }

    /// <summary>One app found below the version floor its manifests declared for it.</summary>
    public sealed record VersionFloorViolation(string AppName, Version FoundVersion, Version RequiredVersion);

    /// <summary>
    /// Scans <paramref name="packageCacheDirs"/> for every Microsoft app named in
    /// <paramref name="versionFloors"/> and reports the ones whose highest found version is
    /// BELOW its declared floor. An app entirely absent from <paramref name="packageCacheDirs"/>
    /// is not a violation here (that's plain absence, already handled by the presence
    /// checks) — this only flags "found, but too old", which is the specific silent gap
    /// issue #2003 is about: a warm set that resolves as present yet doesn't meet what the
    /// manifest actually requires. Pure filesystem scan — no network.
    /// </summary>
    public static IReadOnlyList<VersionFloorViolation> FindVersionFloorViolations(
        IReadOnlyList<string> packageCacheDirs,
        IReadOnlyDictionary<string, Version> versionFloors)
    {
        var violations = new List<VersionFloorViolation>();
        foreach (var (appName, floor) in versionFloors)
        {
            Version? best = null;
            foreach (var dir in packageCacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var appFile in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app"))
                {
                    var m = AlRunner.AppLoader.ReadManifest(appFile);
                    if (m == null) continue;
                    if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(m.Name, appName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || m.Version > best) best = m.Version;
                }
            }
            if (best != null && best < floor)
                violations.Add(new VersionFloorViolation(appName, best, floor));
        }
        return violations;
    }

    /// <summary>
    /// True iff EVERY app in <see cref="KnownNoFallbackPlatformApps"/> is found (any
    /// R2R-ness — this only asks "is it there", not "is it runnable"; that's
    /// CheckPlatformApps' job) somewhere across <paramref name="packageCacheDirs"/>, AT OR
    /// ABOVE the version floor <paramref name="versionFloors"/> declares for it (issue
    /// #2003). A found-but-below-floor app does NOT count as present. Null/empty
    /// <paramref name="versionFloors"/> (the default) preserves the old any-version
    /// presence-only behavior.
    /// </summary>
    public static bool NoFallbackPlatformAppsPresent(
        IReadOnlyList<string> packageCacheDirs,
        IReadOnlyDictionary<string, Version>? versionFloors = null)
        => FindMissingPlatformApps(KnownNoFallbackPlatformApps, packageCacheDirs, versionFloors).Count == 0;

    /// <summary>
    /// The subset of <paramref name="requiredAppNames"/> that is NOT found as a
    /// Microsoft-published <c>.app</c> anywhere across <paramref name="packageCacheDirs"/>
    /// at or above the floor <paramref name="versionFloors"/> declares for it — i.e. the
    /// apps that are genuinely ABSENT (a found-but-below-floor app counts as absent, issue
    /// #2003; a found-but-symbol-only app is a DIFFERENT gap, reported by
    /// <see cref="CheckPlatformApps"/>). Order follows <paramref name="requiredAppNames"/>,
    /// so messages built from it are deterministic. Pure filesystem scan — no network.
    /// </summary>
    public static IReadOnlyList<string> FindMissingPlatformApps(
        IReadOnlyList<string> requiredAppNames,
        IReadOnlyList<string> packageCacheDirs,
        IReadOnlyDictionary<string, Version>? versionFloors = null)
    {
        var missing = new List<string>();
        foreach (var required in requiredAppNames)
        {
            var floor = versionFloors != null && versionFloors.TryGetValue(required, out var f) ? f : null;
            bool found = false;
            foreach (var dir in packageCacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var appFile in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app"))
                {
                    var m = AlRunner.AppLoader.ReadManifest(appFile);
                    if (m == null) continue;
                    if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(m.Name, required, StringComparison.OrdinalIgnoreCase)) continue;
                    if (floor != null && m.Version < floor) continue;
                    found = true;
                    break;
                }
                if (found) break;
            }
            if (!found) missing.Add(required);
        }
        return missing;
    }

    /// <summary>
    /// The full provisioning decision for one invocation: what the manifest needs, what's
    /// already complete in <paramref name="searchDirs"/>, and — combined with the legacy
    /// symbol-only-R2R finding — whether a download should actually happen. Pure — does no I/O.
    /// </summary>
    public sealed record ManifestProvisionDecision(
        bool NeedsPlatformApps,
        bool NeedsTestApps,
        bool PlatformComplete,
        bool TestComplete,
        bool ShouldDownloadPlatform,
        bool ShouldDownloadTest,
        IReadOnlyList<string> UnreadablePackages,
        IReadOnlyList<string> RequiredPlatformApps,
        IReadOnlyList<string> MissingPlatformApps)
    {
        public bool ShouldDownloadAny => ShouldDownloadPlatform || ShouldDownloadTest;
    }

    /// <summary>
    /// Combines manifest-derived need with the legacy symbol-only-R2R finding
    /// (<paramref name="legacySymbolOnlyReport"/> — CheckPlatformApps) to decide whether a
    /// download is actually warranted, given what's already present in
    /// <paramref name="searchDirs"/>. A found-but-symbol-only app is ALWAYS a gap (backward
    /// compatible with issue #1678, even absent a manifest need — e.g. no app.json was
    /// readable). A manifest need with nothing found anywhere is issue #1996's gap:
    /// CheckPlatformApps alone reports that case vacuously "Ok".
    ///
    /// Issue #2003: "present" is no longer presence-alone. <paramref name="manifestRoots"/>
    /// also carries each dependency's declared version, so PlatformComplete/TestComplete
    /// (via NoFallbackPlatformAppsPresent/TestToolkitPresent) now require the found app to
    /// meet that floor too — a warm-but-stale app in <paramref name="searchDirs"/> no longer
    /// reads as complete, which was true here (the initial gate, before any
    /// --auto-provision download decision) just as much as it was in the warm-reuse scan
    /// this issue's repro pointed at.
    /// </summary>
    /// <param name="country">
    /// BC artifact country channel — null (the default) reads <see
    /// cref="BcArtifacts.SelectedCountry"/>, same pattern as <see cref="PlatformAppsDirFor"/>.
    /// Issue #2236: when this resolves to anything other than "w1", every direct
    /// Microsoft-publisher root dependency the manifests declare becomes an extra platform-
    /// need candidate (see DetermineManifestNeeds' extraPlatformNeedCandidates) — a
    /// country-selected download can satisfy any Microsoft app under Extensions/, not just
    /// the w1-curated five, so a bundle depending on e.g. "IRS Forms" now actually triggers
    /// the download that can supply it, instead of needs-detection staying blind to an app
    /// it has never heard of and never downloading anything.
    /// </param>
    public static ManifestProvisionDecision DecideManifestProvisioning(
        IEnumerable<AlRunner.DependencyRef> manifestRoots,
        PlatformAppsReport legacySymbolOnlyReport,
        IReadOnlyList<string> searchDirs,
        string? country = null)
    {
        var rootsList = manifestRoots as ICollection<AlRunner.DependencyRef> ?? manifestRoots.ToList();
        var scan = ScanDependencyEdges(searchDirs);
        var countryNormalized = NormalizeCountry(country);
        IReadOnlyList<string>? extraPlatformNeedCandidates = countryNormalized == "w1"
            ? null
            : rootsList
                .Where(d => string.Equals(d.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        var needs = DetermineManifestNeeds(rootsList, scan.Edges, extraPlatformNeedCandidates);
        // A floor the BC version under provision could never supply must not create a
        // demand no download can clear — see DropUnsatisfiableFloors. The report carries
        // the selected version, which is exactly the version a download would target.
        var versionFloors = DropUnsatisfiableFloors(
            DetermineVersionFloors(rootsList), legacySymbolOnlyReport.Version);
        // Presence is asked of exactly the apps the manifests required — so an app the
        // bundle never names cannot make the set look incomplete, and an app it DOES name
        // cannot be silently exempted from the check.
        var missingPlatformApps = FindMissingPlatformApps(needs.RequiredPlatformApps, searchDirs, versionFloors);
        var platformComplete = missingPlatformApps.Count == 0;
        var testComplete = TestToolkitPresent(searchDirs, versionFloors);
        var shouldDownloadPlatform = !legacySymbolOnlyReport.Ok || (needs.NeedsPlatformApps && !platformComplete);
        var shouldDownloadTest = needs.NeedsTestApps && !testComplete;
        return new ManifestProvisionDecision(
            needs.NeedsPlatformApps, needs.NeedsTestApps, platformComplete, testComplete,
            shouldDownloadPlatform, shouldDownloadTest, scan.UnreadablePackages,
            needs.RequiredPlatformApps, missingPlatformApps);
    }

    /// <summary>
    /// Reads dependency roots from every path in <paramref name="appJsonPaths"/> via
    /// <paramref name="reader"/> (the caller's own app.json parser — kept as a delegate so
    /// this stays a pure I/O-free helper with no duplicate JSON-parsing logic), swallowing
    /// per-manifest read failures instead of throwing. This is a PRE-SCAN: the normal bundle
    /// loader reaches the same manifest moments later and is the one responsible for the
    /// real diagnostic on a malformed/non-object app.json (issue #1996 acceptance criterion
    /// #9) — this pre-scan must never crash the whole invocation over it, it just treats an
    /// unreadable manifest as "nothing declared" and lets the loader speak.
    /// </summary>
    public static IReadOnlyList<AlRunner.DependencyRef> TryReadManifestDependencyRoots(
        IEnumerable<string> appJsonPaths,
        Func<string, IEnumerable<AlRunner.DependencyRef>> reader,
        Action<string>? onError = null)
    {
        var result = new List<AlRunner.DependencyRef>();
        foreach (var path in appJsonPaths)
        {
            try
            {
                result.AddRange(reader(path));
            }
            catch (Exception ex)
            {
                onError?.Invoke($"[provision] manifest pre-scan: skipping unreadable '{path}': {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// What `al-runner provision` prints when it is NOT going to fetch the platform-app
    /// set. Issue #2073 removed an unverified "already present" claim here; issue #2205
    /// found the replacement had swapped it for a different unverified claim — "target
    /// bundle(s) do not need the platform R2R apps set" was printed for a bundle that
    /// demonstrably needed it, because the need was never detected in the first place.
    ///
    /// The honest form states what was CHECKED and what was FOUND, and never asserts that
    /// a need does not exist:
    /// <list type="bullet">
    /// <item>apps were required and all were found → presence is claimed, because it was
    /// verified, and the apps are named;</item>
    /// <item>no app was required → say that the manifests named none, and where we looked,
    /// so a reader who disagrees knows exactly which fact to go check.</item>
    /// </list>
    /// Pure — does no I/O.
    /// </summary>
    public static string BuildPlatformProvisionSkippedMessage(
        IReadOnlyList<string> requiredPlatformApps, IReadOnlyList<string> searchedDirs)
    {
        var where = searchedDirs.Count > 0 ? string.Join(", ", searchedDirs) : "(no package cache directories)";
        if (requiredPlatformApps.Count > 0)
            return "[provision] platform R2R apps already present for the target bundle(s): " +
                   $"{string.Join(", ", requiredPlatformApps)} — found in {where}.";
        return "[provision] no Microsoft platform app is named by the target bundle(s)' app.json " +
               $"(neither `dependencies` nor `application`/`platform`); searched {where} — nothing to fetch.";
    }

    /// <summary>
    /// Loud message for the case DecideManifestProvisioning identifies: the manifest
    /// declares a need, nothing satisfying it was found anywhere, and --auto-provision
    /// was NOT given (issue #1996 acceptance criterion #10: no download without opt-in).
    /// </summary>
    public static string BuildManifestNeedsMissingMessage(
        bool needsPlatform, bool needsTest, IReadOnlyList<string> searchedDirs,
        IReadOnlyList<string>? missingPlatformApps = null)
    {
        var lines = new List<string>
        {
            "This bundle's app.json declares Microsoft dependencies that were not found in",
            "any searched package cache — an empty/incomplete cache is not evidence they're unneeded.",
            "",
        };
        if (needsPlatform)
            lines.Add(missingPlatformApps is { Count: > 0 }
                // Issue #2205: name the apps that are actually absent. "the Microsoft
                // platform-app set" alone left the reader no way to tell which dependency
                // the run was about to die on.
                ? "  Needs: the Microsoft platform-app set — missing from every searched cache: " +
                  string.Join(", ", missingPlatformApps) + "."
                : "  Needs: the Microsoft platform-app set (Base Application / System Application / " +
                  "Business Foundation / Application / Application Test Library).");
        if (needsTest)
            lines.Add("  Needs: the Microsoft test-toolkit set (Business Foundation Test Libraries / " +
                "Library Assert / Test Runner / …).");
        lines.Add("");
        // "Searched: " followed by nothing reads as a missing value rather than as the
        // fact it is — on a genuinely cold cache there is no package cache directory yet,
        // and that is precisely the thing the reader needs told (issue #2205).
        lines.Add(searchedDirs.Count > 0
            ? $"  Searched: {string.Join(", ", searchedDirs)}"
            : "  Searched: nothing — no package cache directory exists yet on this machine.");
        lines.Add("");
        lines.Add("  Resolve it ONE of these ways:");
        lines.Add("");
        lines.Add("  (a) One command (recommended):");
        lines.Add("        al-runner provision");
        lines.Add("      or re-run with --auto-provision.");
        lines.Add("");
        lines.Add("  (b) Force-download a specific set:");
        if (needsPlatform)
            lines.Add("        al-runner provision --platform-apps --bc-version <full-version>");
        if (needsTest)
            lines.Add("        al-runner provision --test-apps --bc-version <full-version>");
        return string.Join(Environment.NewLine, lines);
    }

    public sealed record Report(string Version, string ServiceTierDir, IReadOnlyList<string> MissingFiles)
    {
        public bool Ok => MissingFiles.Count == 0;

        /// <summary>
        /// One loud, self-contained message: names every missing file + its full path, the
        /// exact manual command to fetch them, and the one-command auto-resolve. Detailed
        /// enough for a human or an agent to fix by hand.
        /// </summary>
        public string ToDetailedMessage(string? projectPathForProvisionCmd = null)
        {
            var provisionTarget = projectPathForProvisionCmd is { Length: > 0 }
                ? $" \"{projectPathForProvisionCmd}\""
                : "";
            var lines = new List<string>
            {
                $"BC {Version} engine artifacts are incomplete — the runner will not auto-download.",
                $"Expected under: {ServiceTierDir}",
                "",
                "Missing:",
            };
            foreach (var f in MissingFiles)
                lines.Add($"  - {Path.Combine(ServiceTierDir, f)}");
            lines.Add("");
            lines.Add("Resolve it ONE of these ways:");
            lines.Add("");
            lines.Add("  (a) One command (recommended) — the runner downloads the missing pieces:");
            lines.Add($"        al-runner provision{provisionTarget}");
            lines.Add($"      or re-run your command with --auto-provision.");
            lines.Add("");
            lines.Add("  (b) Force-download the full service-tier closure for this version:");
            lines.Add($"        al-runner provision --service-tier --bc-version {Version}");
            lines.Add("");
            lines.Add("  (c) Point the runner at an existing artifact dir with --artifact-path <dir>,");
            lines.Add("      or select a different cached version with --bc-version <ver>.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Check whether the given version's artifact <paramref name="serviceTierDir"/> holds a
    /// complete engine closure. Never throws; returns a <see cref="Report"/> listing what
    /// (if anything) is missing.
    /// </summary>
    public static Report Check(string version, string serviceTierDir)
    {
        var missing = new List<string>();
        if (!Directory.Exists(serviceTierDir))
        {
            // The whole dir is gone — report every required file as missing.
            missing.AddRange(CoreEngineDlls);
            missing.Add(ClosureSentinel);
            return new Report(version, serviceTierDir, missing);
        }
        foreach (var dll in CoreEngineDlls)
            if (!File.Exists(Path.Combine(serviceTierDir, dll)))
                missing.Add(dll);
        if (!File.Exists(Path.Combine(serviceTierDir, ClosureSentinel)))
            missing.Add(ClosureSentinel);
        return new Report(version, serviceTierDir, missing);
    }

    /// <summary>
    /// Download the engine service-tier closure for <paramref name="version"/> into
    /// <paramref name="serviceTierDir"/> (the full /service/ closure — the same set the
    /// manual `service-tier` command fetches). Returns true on success. This is the
    /// opt-in auto-resolve; callers gate it behind `al-runner provision` / `--auto-provision`.
    /// </summary>
    public static bool AutoProvision(string version, string serviceTierDir, Action<string>? log = null)
    {
        var logf = log ?? Console.Error.WriteLine;
        logf($"[provision] downloading BC {version} engine service-tier closure → {serviceTierDir}");
        var rc = ArtifactDownloader.ServiceTier(version, serviceTierDir, logf);
        if (rc != 0)
        {
            logf($"[provision] download failed (exit {rc}). See messages above.");
            return false;
        }
        var after = Check(version, serviceTierDir);
        if (!after.Ok)
        {
            logf($"[provision] still incomplete after download; missing: {string.Join(", ", after.MissingFiles)}");
            return false;
        }
        logf($"[provision] BC {version} engine artifacts complete.");
        return true;
    }
}
