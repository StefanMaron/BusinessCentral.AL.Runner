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
            lines.Add("  (b) Download Microsoft platform apps only:");
            // Use the FIRST missing app's own real version — not a truncation of Version
            // (the engine version), which can be a different minor and would 404 against
            // the artifact CDN (it needs a FULL artifact version, e.g. 28.2.50931.52786).
            var suggestVer = Issues.Count > 0 ? Issues[0].AppVersion : "<full-version, e.g. 28.2.50931.52786>";
            lines.Add($"        dotnet run --project tools/DownloadArtifacts -- platform-apps {suggestVer} \"<package-cache-dir>\"");
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
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var bundle in bundlePaths)
        {
            if (string.IsNullOrEmpty(bundle) || !Directory.Exists(bundle)) continue;
            IEnumerable<string> found;
            try { found = Directory.EnumerateDirectories(bundle, ".alpackages", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var dir in found)
                if (seen.Add(dir))
                    result.Add(dir);
        }
        return result;
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
                foreach (var appFile in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
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
    /// Derives the BC major.minor to auto-provision platform apps for. The engine is
    /// version-agnostic w.r.t. the R2R apps it dispatches to at runtime, so the minor to
    /// download is the one the MISSING apps actually need (carried in
    /// <paramref name="report"/>.Issues[0].AppVersion) — NOT the engine's own
    /// <paramref name="fallbackVersion"/> (SelectedVersion), which can be a different minor
    /// (e.g. engine 28.1 running 28.2 R2R business logic). Falls back to
    /// <paramref name="fallbackVersion"/>'s major.minor when there are no issues. Pure —
    /// does no I/O.
    /// </summary>
    public static string DeriveProvisionMajorMinor(PlatformAppsReport report, string fallbackVersion)
    {
        var source = report.Issues.Count > 0 ? report.Issues[0].AppVersion : fallbackVersion;
        var parts = source.Split('.');
        return parts.Length >= 2 ? string.Join(".", parts.Take(2)) : source;
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
            $"    dotnet run --project tools/DownloadArtifacts -- platform-apps {appVersion} \"<package-cache-dir>\"",
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
    /// </summary>
    public static bool TestToolkitPresent(IReadOnlyList<string> packageCacheDirs)
    {
        foreach (var dir in packageCacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var appFile in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
            {
                var m = AlRunner.AppLoader.ReadManifest(appFile);
                if (m == null) continue;
                if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(TestToolkitSentinelApp, m.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Derives the BC major.minor to auto-provision FROM WHAT'S ALREADY IN THE CACHE: scans
    /// <paramref name="packageCacheDirs"/> for a Microsoft "Base Application" or "System
    /// Application" .app and returns its major.minor. Used when there's no
    /// <see cref="PlatformAppsReport"/> issue to derive from (e.g. platform apps are already
    /// R2R-complete but the test toolkit is still missing) — we still need SOME minor to
    /// resolve a full artifact version for the test-toolkit download, and the platform apps
    /// already in the cache are the most reliable signal of which minor this project targets.
    /// Falls back to <paramref name="fallbackVersion"/>'s major.minor when no such app is found.
    /// </summary>
    public static string DerivePresentPlatformMajorMinor(
        IReadOnlyList<string> packageCacheDirs, string fallbackVersion)
    {
        foreach (var dir in packageCacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var appFile in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
            {
                var m = AlRunner.AppLoader.ReadManifest(appFile);
                if (m == null) continue;
                if (!string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(m.Name, "Base Application", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(m.Name, "System Application", StringComparison.OrdinalIgnoreCase))
                    continue;
                var v = m.Version.ToString();
                var vparts = v.Split('.');
                return vparts.Length >= 2 ? string.Join(".", vparts.Take(2)) : v;
            }
        }
        var parts = fallbackVersion.Split('.');
        return parts.Length >= 2 ? string.Join(".", parts.Take(2)) : fallbackVersion;
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
    /// </summary>
    public static string PlatformAppsDirFor(string artifactsRootDir, string fullVersion)
        => Path.Combine(artifactsRootDir, fullVersion, "platform-apps");

    /// <summary>
    /// Runner-owned directory to download the Microsoft test toolkit (Business Foundation
    /// Test Libraries, Application Test Library, Library Assert, Test Runner, …) into.
    /// </summary>
    public static string TestAppsDirFor(string artifactsRootDir, string fullVersion)
        => Path.Combine(artifactsRootDir, fullVersion, "test-apps");

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
            lines.Add("  (b) Manually — fetch the full service-tier closure for this version:");
            lines.Add($"        dotnet run --project tools/DownloadArtifacts -- service-tier {Version} \"{ServiceTierDir}\"");
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
