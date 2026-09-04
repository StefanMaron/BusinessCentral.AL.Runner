namespace AlRunner;

// The `provision` CLI subcommand and the artifact/package-cache resolution it
// shares with auto-provisioning: RunExplicitProvisionModes, EnsureTestToolkitProvisioned,
// version/artifact-path resolution. Split out of Program.cs (#2665) -- purely static,
// no captured state.
internal static partial class ProgramSupport
{

    // Expands user-provided --package-cache dirs: returns each dir that exists, plus
    // any bcartifacts platform/Applications and platform/ModernDev dirs auto-discovered
    // from the same artifact version root. Deduplicates so the same dir isn't listed
    // twice if the user already passed it explicitly.
    // This ensures that even when only the ISV .alpackages and w1/Extensions are passed,
    // the higher-version platform test packages (e.g. Tests-TestLibraries v28.1 in
    // platform/Applications/BaseApp/Test) are visible to the version-aware resolver.
    internal static IEnumerable<string> ExpandPackageCacheDirs(IEnumerable<string> userDirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in userDirs)
        {
            if (!Directory.Exists(dir)) continue;
            if (seen.Add(dir)) yield return dir;
            foreach (var extra in BcArtifactTestDirs(dir))
                if (seen.Add(extra)) yield return extra;
        }
    }

    // Auto-discovers bcartifacts platform dirs from an explicit --package-cache path.
    // Gated to paths inside ~/.bcartifacts.cache/ so corpus runs and non-bcartifacts
    // cache dirs are unaffected. Walks up from the given dir to find the artifact
    // version root (the child of sandbox/<version>/ that has a platform/ subdirectory)
    // and yields platform/Applications and platform/ModernDev if they exist.
    internal static IEnumerable<string> BcArtifactTestDirs(string cacheDir)
    {
        // Cross-platform home (POSIX HOME is null on Windows — see AlRunnerPaths).
        var home = AlRunner.Infrastructure.AlRunnerPaths.UserHome;
        if (string.IsNullOrEmpty(home)) yield break;

        var bcRoot = Path.GetFullPath(Path.Combine(home, ".bcartifacts.cache"));
        var full = Path.GetFullPath(cacheDir);

        // Only auto-expand dirs that are inside the bcartifacts cache root.
        if (!full.StartsWith(bcRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, bcRoot, StringComparison.OrdinalIgnoreCase))
            yield break;

        // Walk up from cacheDir toward bcRoot, stopping at the first dir that has
        // a platform/ subdirectory — that is the artifact version root.
        var dir = full;
        while (dir.Length > bcRoot.Length)
        {
            var platApps = Path.Combine(dir, "platform", "Applications");
            if (Directory.Exists(platApps))
            {
                yield return platApps;
                yield break;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) yield break;
            dir = parent;
        }
    }

    // ── The one shadow-re-exec decision ────────────────────────────────────────────────
    // Returns null when this process may proceed in place, or the child's exit code when it
    // handed off to a shadow-runtime process (in which case the caller must return that code
    // immediately and touch no BC type).
    //
    // #2065: this used to be written out inline at the single call site in the main bundle-run
    // flow, and `--precompile` — which dispatches near the top of Main, LONG before that call
    // site — had no equivalent. It went straight to NclCecilRewrite.RewriteInPlace against
    // `AppContext.BaseDirectory`, which on any install that does not ship Ncl.dll (i.e. every
    // install since #2023/#2026, including AlRunner's own build output, where
    // Directory.Build.targets strips it) CREATES the file instead of replacing one. Two
    // separate costs, and neither is theoretical — both were measured while closing #2065:
    //
    //   * It does not help the process doing the writing. CoreCLR fixes the
    //     trusted-platform-assemblies list in the native host from the literal on-disk contents
    //     of AppContext.BaseDirectory BEFORE any managed code runs (see NclShadowRuntime's class
    //     doc), so a file written a few statements into RunPrecompile is already too late. The
    //     `--precompile` run only worked at all because DependencyLoader's Resolving fallback
    //     then happened to serve the freshly written copy — and on a genuine cache HIT there is
    //     no re-exec to recover, so the whole thing rested on a side effect.
    //   * It permanently changes what NclShadowRuntime.NeedsShadow answers for that directory,
    //     so every LATER invocation from it silently stops taking the hop. The runner then
    //     behaves differently in a used checkout than in a clean one — the exact class of
    //     problem a stale shadow cache caused when it faked a packaging bug here.
    //
    // Two code paths writing the same state where only one held the invariant is the defect, so
    // there is now exactly one place that decides. Any future pre-dispatch subcommand that needs
    // BC types calls this first.
    internal static int? TryShadowReexec(string? variantSwapDir)
    {
        if (!(AlRunner.Infrastructure.NclShadowRuntime.NeedsShadow(AppContext.BaseDirectory) || variantSwapDir != null)
            || Environment.GetEnvironmentVariable("AL_RUNNER_NCL_SHADOW_DONE") == "1")
            return null;

        var srcDirForShadow = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
        var shadowDll = AlRunner.Infrastructure.NclShadowRuntime.EnsureShadowDir(
            AppContext.BaseDirectory, srcDirForShadow, variantSwapDir);
        var dotnetMuxer = AlRunner.Infrastructure.NclShadowRuntime.FindDotnetMuxer();

        var psi = new System.Diagnostics.ProcessStartInfo(dotnetMuxer) { UseShellExecute = false };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(shadowDll);
        // argv[0] is THIS process's own entry path (apphost exe, or the dll path the
        // dotnet muxer forwarded) — never a user arg, and irrelevant here since we've
        // already picked the child's entry point explicitly above.
        var argv = RewriteArtifactPathArg(Environment.GetCommandLineArgs());
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        psi.Environment["AL_RUNNER_NCL_SHADOW_DONE"] = "1";

        // #2034: this line explains why a second process is about to launch — a
        // genuinely operational fact, not an internal Cecil-rewrite diagnostic — so it
        // uses the exempted `[reexec]` tag rather than `[Cecil]`. Under `[Cecil]`, Log's
        // filter suppressed a real, live re-exec silently: the shadow dir was built, the
        // child launched, and nothing on stderr said why.
        //
        // #2239: that reasoning still holds for someone debugging a re-exec — hence
        // `[reexec]`, not a plain internal tag, so --verbose still surfaces it — but a
        // clean run does not need to know its own process topology to read its test
        // results, so this is now gated behind --verbose like the rest of this file's
        // startup bookkeeping.
        if (AlRunner.Log.Verbose)
            Console.Error.WriteLine(variantSwapDir != null
                ? "[reexec] Re-execing into a shadow runtime dir with the matching BC-minor engine variant"
                : "[reexec] Ncl.dll not shipped in this install — re-execing into a shadow runtime dir that has it");
        AlRunner.Infrastructure.PhaseLog.MarkReexecParent();
        using var shadowChild = System.Diagnostics.Process.Start(psi)!;
        shadowChild.WaitForExit();
        return shadowChild.ExitCode;
    }

    // Rewrite a forwarded argv so that `--artifact-path <dir>` becomes `--bc-version <ver>`
    // when <dir> is a version-named child of the standard artifacts cache. Re-exec children
    // then take the byte-identical code path as `--bc-version` (the explicit-root selection
    // branch otherwise perturbs BC's R2R-precompiled startup bind enough to trigger a
    // teardown access violation — see MEMORY.md "R2R-layout-perturbation native AV"). A
    // path OUTSIDE the standard cache is left as `--artifact-path` (the child needs it).
    internal static string[] RewriteArtifactPathArg(string[] argv)
    {
        var outv = new List<string>(argv.Length);
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--artifact-path" && i + 1 < argv.Length)
            {
                string? ver = null;
                try { ver = AlRunner.Infrastructure.BcArtifacts.TryTranslateArtifactPathToVersion(argv[i + 1]); }
                catch (InvalidOperationException) { ver = null; }
                if (ver != null) { outv.Add("--bc-version"); outv.Add(ver); i++; continue; }
            }
            outv.Add(argv[i]);
        }
        return outv.ToArray();
    }

    // Default cache: the selected BC version (BcArtifacts.SelectedVersion — latest in the
    // artifacts cache, or the --bc-version / --artifact-path override) under
    // ~/.bcartifacts.cache/sandbox/ + the curated symbol set under
    // ~/.local/share/al-runner/symbols/. These two trees may carry a different *patch*
    // level than the artifacts tree (e.g. sandbox 28.1.x vs artifacts 28.1.y), so we match
    // on the selected major.minor prefix and pick the highest such version (System.Version
    // sort — the old StringComparer.Ordinal sort mis-ordered e.g. "28.1.9" > "28.1.10").
    internal static IEnumerable<string> DefaultPackageCacheDirs()
    {
        // Cross-platform home (POSIX HOME is null on Windows — see AlRunnerPaths).
        var home = AlRunner.Infrastructure.AlRunnerPaths.UserHome;
        if (string.IsNullOrEmpty(home)) yield break;

        var sel = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
        var mmPrefix = $"{sel.Major}.{sel.Minor}";

        var bcRoot = Path.Combine(home, ".bcartifacts.cache", "sandbox");
        var bcLatest = SelectVersionDirOrNull(bcRoot, mmPrefix);
        if (bcLatest != null)
        {
            // Issue #2236: this tree is VS Code's AL-extension symbol cache, not ours — we only
            // read it, never download into it — but it uses the SAME sandbox/<ver>/<channel>/
            // layout our own artifact CDN does, one channel folder per country. A machine that
            // already has a country-localized project's symbols downloaded there (VS Code's own
            // "AL: Download Symbols" honors app.json/the workspace's own settings, which can
            // target a country other than w1) would never be found by a hardcoded "w1" lookup —
            // the exact shape of gap this issue exists to close, one directory over. Scan the
            // SELECTED country's own channel folder, not both: mixing w1 and a country's folder
            // here would risk the identical "which duplicate basename wins" ambiguity #2236's
            // own Extensions/-vs-Applications.<CC>/ fix exists to avoid for our own cache.
            var localizedExt = Path.Combine(bcLatest, AlRunner.Infrastructure.BcArtifacts.SelectedCountry, "Extensions");
            if (Directory.Exists(localizedExt)) yield return localizedExt;
            var platApps = Path.Combine(bcLatest, "platform", "Applications");
            if (Directory.Exists(platApps)) yield return platApps;
            // The `System` platform-symbols app (Microsoft/System) ships here, not in
            // w1/Extensions. The resolver scans *.app recursively, so the ModernDev
            // root suffices despite the version-numbered / case-varying subpath.
            var modernDev = Path.Combine(bcLatest, "platform", "ModernDev");
            if (Directory.Exists(modernDev)) yield return modernDev;
        }

        var symRoot = Path.Combine(home, ".local", "share", "al-runner", "symbols");
        var symLatest = SelectVersionDirOrNull(symRoot, mmPrefix);
        if (symLatest != null) yield return symLatest;

        // The provisioned MS test toolkit / platform R2R runtime apps (see
        // EnsureTestToolkitProvisioned / --auto-provision, issue #1653). Scanned by default so
        // a test bundle whose app.json depends on Library Assert / Test Runner / Any / System
        // Application resolves them without --package-cache, and so a --auto-provision run on
        // one invocation is visible on a later run that omits --auto-provision.
        //
        // Issue #2234: scan every patch directory sharing this major.minor, not just `sel`'s
        // own exact patch — #2226 (separate, still open) can leave `provision`'s platform-app
        // and test-app sub-steps under DIFFERENT patch directories of the same major.minor,
        // and this used to look only at `sel`'s own patch, missing a sibling directory the
        // auto-provision reuse scan (FindWarmProvisionedVersion) found and reporting
        // "missing" on every subsequent --no-auto-provision run even right after `provision`
        // had just completed successfully.
        foreach (var dir in AlRunner.Infrastructure.ProvisioningCheck.CollectRunnerOwnedProvisionDirs(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, mmPrefix))
            yield return dir;
    }

    // Highest version-named child of <root> matching <versionPrefix> (System.Version sort),
    // or null if the root is absent or has no matching version dir. Unlike the artifact
    // helper this returns null rather than throwing: these caches are optional augmentation
    // of the artifact dir, and a missing sandbox/symbols tree is not fatal (the corpus runs
    // from the artifact dir alone). The artifact dir itself fails loud via BcArtifacts.
    internal static string? SelectVersionDirOrNull(string root, string versionPrefix)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(root, versionPrefix);
        }
        catch (InvalidOperationException)
        {
            // No matching version in this optional cache — fine.
            return null;
        }
    }

    /// <summary>
    /// Issue #1996 (AC #4/#5): before resolving "latest" from the CDN index for a manifest-app
    /// download, prefer a full version ALREADY cached under <paramref name="artifactsRootDir"/>
    /// whose major.minor matches <paramref name="majorMinorPrefix"/> and whose needed set(s) are
    /// already complete — highest patch first. Returns null when no such warm version exists
    /// (the caller then falls back to CDN resolution). Reuse-only: never downloads.
    ///
    /// Issue #2003: "complete" used to mean presence alone — a warm set at the same
    /// major.minor but an OLDER patch than the bundle's app.json declares (<paramref
    /// name="versionFloors"/>) was reused unconditionally, and the run failed later on a
    /// compile diagnostic that pointed at the test code instead of the stale provisioning.
    /// NoFallbackPlatformAppsPresent/TestToolkitPresent now reject a candidate whose found app
    /// is below its floor, so this loop naturally skips it and falls through to the next
    /// (older) candidate, and eventually to CDN resolution if none qualify. When a candidate is
    /// rejected specifically because it was found-but-stale (not merely absent), <paramref
    /// name="onRejected"/> is told which app, the version found, and the version required —
    /// a silent re-download is better than reusing something too old, but a message is better
    /// than both. Null/empty <paramref name="versionFloors"/> (the default) reproduces the old
    /// presence-only behavior verbatim (AC #4).
    /// </summary>
    internal static string? FindWarmProvisionedVersion(
        string artifactsRootDir, string majorMinorPrefix,
        IReadOnlyList<string> requiredPlatformApps, bool needTest,
        IReadOnlyDictionary<string, Version>? versionFloors = null,
        Action<string>? onRejected = null)
    {
        if (!Directory.Exists(artifactsRootDir)) return null;
        var candidates = Directory.EnumerateDirectories(artifactsRootDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n)
                && (n == majorMinorPrefix || n!.StartsWith(majorMinorPrefix + ".", StringComparison.Ordinal)))
            .Select(n => (Name: n!, Ver: Version.TryParse(n, out var v) ? v : null))
            .Where(t => t.Ver != null)
            .OrderByDescending(t => t.Ver)
            .Select(t => t.Name);

        foreach (var name in candidates)
        {
            var platformDir = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(artifactsRootDir, name);
            var testDir = AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(artifactsRootDir, name);
            // Issue #2205: "is this warm set usable" asks for the apps this bundle actually
            // requires, not a fixed one-element list. An empty requirement is vacuously ok.
            var platformOk = AlRunner.Infrastructure.ProvisioningCheck.FindMissingPlatformApps(
                requiredPlatformApps, new[] { platformDir }, versionFloors).Count == 0;
            var testOk = !needTest || AlRunner.Infrastructure.ProvisioningCheck.TestToolkitPresent(
                new[] { testDir }, versionFloors);
            if (platformOk && testOk) return name;

            if (versionFloors is { Count: > 0 } && onRejected != null)
            {
                var violations = AlRunner.Infrastructure.ProvisioningCheck.FindVersionFloorViolations(
                    new[] { platformDir, testDir }, versionFloors);
                foreach (var v in violations)
                    onRejected(
                        $"[provision] warm set '{name}' rejected: '{v.AppName}' found at v{v.FoundVersion}, " +
                        $"but this bundle's app.json requires >= v{v.RequiredVersion}.");
            }
        }
        return null;
    }

    // #1824: de-duplicated. This used to be its own copy of the walk-up loop, kept in sync
    // by hand with WatchSource.FindBucketRoot's byte-identical copy (WatchSource couldn't
    // call this one — top-level-statement local functions, being nested inside the
    // synthesized <Main>$ method, aren't reachable from another file/class regardless of
    // accessibility modifiers). WatchSource.FindBucketRoot was promoted to `internal` (see
    // its own doc comment) and is now the single shared implementation; this delegates
    // rather than reimplementing, so the two can no longer silently drift out of sync. All
    // 8 call sites below are unchanged — only this function's body moved.
    internal static string? FindBucketRoot(string bundlePath) => WatchSource.FindBucketRoot(bundlePath);

    // Scan the given dirs for Microsoft PLATFORM apps (Application/System/Base Application/
    // System Application/Business Foundation) and return one sidecar DepEntry per distinct
    // app (real AppId + version read from the .app manifest). These apps enter a source dep's
    // compile via the raw package scan of its own .alpackages even when they are NOT part of
    // the resolved spec closure (they are synthesized as Optional implicit roots). A dependent
    // app can therefore only link the types they carry — e.g. `Codeunit "Temp Blob"`
    // (System Application), `Enum "Copilot Capability"` (platform System) — if the dep's
    // sidecar declares them. Without this a dependent sees those parameter types as
    // __MissingTypeSymbol__ (AL0133). Scoped to the dep's OWN .alpackages (not the global
    // package cache) to keep the scan bounded and the declared closure faithful to what the
    // dep actually vendored. See DepsSidecarWriter.BuildClosure and issue #1546.
    internal static IEnumerable<DepsSidecarWriter.DepEntry> ScanVendoredPlatformApps(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            // SafeDirectoryScan, not a try around Directory.EnumerateFiles: the latter is lazy,
            // so the catch guarded only the enumerator's construction and an unreadable
            // subdirectory threw out of the foreach below. #2206.
            var apps = AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app");
            foreach (var app in apps)
            {
                var m = AppLoader.ReadManifest(app);
                if (m == null) continue;
                if (AlRunner.DependencyResolver.IsMicrosoftPlatformApp(m.Name, m.Publisher))
                    yield return new DepsSidecarWriter.DepEntry(m.Publisher, m.Name, m.Version, m.AppId);
            }
        }
    }

    // Derive the BC MAJOR version the target project is built for, from the first bundle's
    // app.json `application` field (falling back to `platform`). Used to default the BC
    // artifact selection when the user gave neither --bc-version nor --artifact-path, so the
    // runner picks the cache version matching the project instead of blindly latest-in-cache
    // (a stray higher-minor download must not silently become the default). Returns the MAJOR
    // as a selection prefix (e.g. "28") — the MAJOR-only engine-consistency contract means any
    // cached minor within that major is interchangeable (verified 28.1<->28.2). Returns null
    // when no app.json / no version field is found (caller then falls back to latest-in-cache).
    internal static string? TryDeriveBcMajorFromProject(IEnumerable<string> bundlePaths)
    {
        foreach (var bundle in bundlePaths)
        {
            string abs;
            try { abs = Path.GetFullPath(bundle); } catch { continue; }
            var root = FindBucketRoot(abs) ?? (Directory.Exists(abs) ? abs : Path.GetDirectoryName(abs));
            if (string.IsNullOrEmpty(root)) continue;
            var appJson = Path.Combine(root, "app.json");
            if (!File.Exists(appJson)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJson));
                var r = doc.RootElement;
                foreach (var field in new[] { "application", "platform" })
                {
                    if (r.TryGetProperty(field, out var fv)
                        && fv.ValueKind == System.Text.Json.JsonValueKind.String
                        && Version.TryParse(fv.GetString(), out var v)
                        && v.Major > 0)
                        return v.Major.ToString();
                }
            }
            catch { /* unparseable manifest — fall through to next bundle / latest-in-cache */ }
        }
        return null;
    }

    // Issue #2085: `al-runner provision --platform-apps|--test-apps|--service-tier [--force]`
    // — a tool-install-valid replacement for `dotnet run --project tools/DownloadArtifacts --
    // <mode> <ver> <dir>`, whose whole body is a switch over the same
    // AlRunner.Provisioning.ArtifactDownloader methods this calls. That project ships only as
    // source (never part of a packaged `dotnet tool install`), so a user without a checkout had
    // no way to reach it. Downloads straight into the canonical directory each mode already
    // resolves to at runtime (BcArtifacts.ArtifactDirFor / ProvisioningCheck.PlatformAppsDirFor /
    // TestAppsDirFor) — no need-detection, no bundle scan, just "fetch this set for this
    // version." `--force` re-downloads even when the directory already looks populated;
    // without it, a populated directory is left alone (mirrors EnsureTestToolkitProvisioned's
    // existing "already present" short-circuit).
    internal static int RunExplicitProvisionModes(string? bcVersionArg, List<string> bundles,
        bool platformApps, bool testApps, bool serviceTier, bool force, string? resolveVersionPrefix)
    {
        // #2208: every failure return below is 2 ("execution error"), never 1. `provision`
        // does not run a single test, so 1 — the documented exit ladder's "at least one test
        // failed or errored" — would be a lie about what happened here; 2 is what every other
        // "couldn't get to a run at all" path in this file already uses (e.g. "BC version
        // selection failed: ..." further up).
        string? full;
        if (resolveVersionPrefix != null)
        {
            var resolved = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
                resolveVersionPrefix, m => Console.Error.WriteLine($"[provision] {m}"));
            if (resolved == null)
            {
                Console.Error.WriteLine($"[provision] could not resolve a full BC version for prefix '{resolveVersionPrefix}'.");
                return 2;
            }
            Console.WriteLine(resolved); // stdout for script/agent consumption, mirrors tools/DownloadArtifacts
            // #2560: used to return 0 here unconditionally, even when the caller ALSO passed
            // --platform-apps/--test-apps/--service-tier -- `provision --resolve-version 28.1
            // --platform-apps` printed a version and downloaded nothing, silently discarding
            // half the command with no indication anything was skipped. Honor the combination
            // instead of discarding it: a resolved version stands in for --bc-version and the
            // named sub-steps still run against it.
            if (!platformApps && !testApps && !serviceTier)
                return 0;
            full = resolved;
        }
        else
        {
            full = ResolveFullVersionForExplicitProvision(bcVersionArg, bundles);
            if (full == null)
                return 2; // the resolver already printed a loud, named reason
        }

        bool anyFailed = false;
        if (serviceTier)
        {
            var dir = AlRunner.Infrastructure.BcArtifacts.ArtifactDirFor(full);
            anyFailed |= ForceProvisionMode("BC service-tier engine DLLs", dir, full, force,
                d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*.dll").Any(),
                (v, d, log) => AlRunner.Provisioning.ArtifactDownloader.ServiceTier(v, d, log)) != 0;
        }
        if (platformApps)
        {
            var dir = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
                AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, full);
            anyFailed |= ForceProvisionMode("Microsoft platform apps", dir, full, force,
                d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*.app").Any(),
                (v, d, log) => AlRunner.Provisioning.ArtifactDownloader.PlatformApps(
                    v, d, AlRunner.Infrastructure.BcArtifacts.SelectedCountry, log)) != 0;
        }
        if (testApps)
        {
            var dir = TestAppsDirFor(full);
            // #2558: this is the exact command the issue reports ("al-runner provision
            // --test-apps can exit 0 over a partial extraction") — unlike the other two
            // modes above, "any *.app file exists" is not a faithful completeness check for
            // the test toolkit specifically: an interrupted extraction that landed one
            // country test app but not the real sentinel (TestToolkitPresent /
            // TestToolkitSentinelApp) used to read as complete forever, and a download that
            // reports rc == 0 without the sentinel landing used to be treated as success.
            anyFailed |= ForceProvisionMode("Microsoft test-toolkit apps", dir, full, force,
                d => AlRunner.Infrastructure.ProvisioningCheck.TestToolkitPresent(new[] { d }),
                (v, d, log) => AlRunner.Provisioning.ArtifactDownloader.TestApps(v, d, log)) != 0;
        }
        return anyFailed ? 2 : 0;
    }

    // Shared by every explicit provision mode: skip the download when <paramref name="isPresent"/>
    // says the canonical directory already looks complete (unless --force), otherwise run
    // <paramref name="download"/> and report success/failure. Named per-mode so the log lines
    // read like the rest of `[provision]` output, not a generic "done"/"failed".
    //
    // #2558: <paramref name="isPresent"/> is also re-checked AFTER a download that reports
    // rc == 0 — a download delegate can report success as soon as it wrote ANYTHING, silently
    // skipping entries it could not fetch, so rc == 0 alone does not mean the expected content
    // actually landed. This was `al-runner provision --test-apps`'s exact reported bug: its
    // predicate used to be "does any *.app file exist in the directory", so a partial
    // extraction (e.g. one leftover country test app but not the toolkit's real sentinel) read
    // as complete forever, and a download that "succeeded" without the sentinel landing was
    // never caught.
    internal static int ForceProvisionMode(string label, string outputDir, string fullVersion, bool force,
        Func<string, bool> isPresent, Func<string, string, Action<string>, int> download)
    {
        if (!force && isPresent(outputDir))
        {
            Console.Error.WriteLine($"[provision] {label} already present at {outputDir} for BC {fullVersion} — skipping (pass --force to re-download).");
            return 0;
        }
        Console.Error.WriteLine($"[provision] fetching {label} for BC {fullVersion} → {outputDir}");
        try
        {
            var rc = download(fullVersion, outputDir, m => Console.Error.WriteLine($"[provision] {m}"));
            if (rc != 0)
            {
                Console.Error.WriteLine($"[provision] warning: {label} download failed for BC {fullVersion}.");
                return rc;
            }
            if (!isPresent(outputDir))
            {
                Console.Error.WriteLine(
                    $"[provision] {label} download reported success but the expected content is " +
                    $"still missing from {outputDir} for BC {fullVersion}.");
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[provision] warning: {label} download failed for BC {fullVersion}: {ex.Message}");
            return 1;
        }
    }

    // Issue #2208: shared by ResolveFullVersionForExplicitProvision and RunProvisioning's own
    // no-`--bc-version` branch. Resolves the version to target from the ENGINE's own build —
    // matching the run path's default selection (#2077) — rather than the project's app.json,
    // which states a floor ("application": "27.0.0.0") and not the version to provision
    // against. `BcArtifacts.EngineMajor(AppContext.BaseDirectory)` (the OLD source both call
    // sites used) requires `Microsoft.Dynamics.Nav.Ncl.dll` to be physically present in bin/,
    // which is FALSE in the ordinary shadow-copy/re-exec install layout — so it silently
    // returned null and both callers fell through past the engine entirely, either giving up
    // ("cannot determine which BC version to provision" despite the binary printing the exact
    // build on every other code path) or deriving the major from whichever bundle happened to
    // be on the command line, downloading a completely different major's artifact set.
    // `BcArtifacts.EngineBuiltVersion()` is baked in at compile time (an AssemblyMetadata
    // attribute) and needs nothing on disk, so it answers the question unconditionally.
    // Returns null only when the engine's build version is genuinely unknown (a stripped/older
    // binary with neither the attribute nor a shipped Ncl.dll) or the artifacts root itself
    // can't be resolved — the caller falls back to prefix-based resolution in that case.
    internal static string? ResolveDefaultProvisionVersion(List<string> bundles, Action<string> log)
    {
        var engineVersion = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? AlRunner.Infrastructure.BcArtifacts.EngineVersion(AppContext.BaseDirectory);
        if (engineVersion == null)
            return null;

        string full;
        string tier;
        try
        {
            full = AlRunner.Infrastructure.BcArtifacts.DefaultProvisionTarget(
                engineVersion, AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, out tier, log);
        }
        catch (InvalidOperationException)
        {
            return null; // ArtifactsRootDir unresolvable — caller falls back to prefix-based resolution
        }
        log($"no --bc-version given — targeting BC {full} (this binary's own engine build is " +
            $"{engineVersion}; tier '{tier}'). Override with --bc-version.");

        // The project's app.json is a CROSS-CHECK only, never the source of the answer — see
        // the doc comment above. #2210: same note as the main auto-select path (see
        // BcArtifacts.DescribeCrossMajorNote and Log.Verbose gating there) — measured no
        // divergence and no compatibility hazard, so this is --verbose-only here too, not
        // printed on an ordinary `provision` run.
        if (AlRunner.Log.Verbose)
        {
            var projMajor = TryDeriveBcMajorFromProject(bundles);
            var crossMajorNote = AlRunner.Infrastructure.BcArtifacts.DescribeCrossMajorNote(projMajor, engineVersion.Major);
            if (crossMajorNote != null) log($"note: {crossMajorNote}");
        }
        return full;
    }

    // Resolves the full 4-part BC version to target for an EXPLICIT provision mode
    // (--platform-apps/--test-apps/--service-tier). Deliberately mirrors RunProvisioning's own
    // resolution (explicit --bc-version, else the engine's own build via
    // ResolveDefaultProvisionVersion, else the target bundle's app.json major as a last
    // resort; prefer an already-cached matching version, else resolve the latest full version
    // from the CDN) — kept as a separate small function rather than sharing RunProvisioning's
    // inline block because that block's own success message ("verifying completeness")
    // describes what RunProvisioning does NEXT (an engine-closure completeness check), which
    // does not apply here.
    internal static string? ResolveFullVersionForExplicitProvision(string? bcVersionArg, List<string> bundles)
    {
        if (bcVersionArg != null && System.Version.TryParse(bcVersionArg, out var maybeFull) && maybeFull.Revision >= 0
            && bcVersionArg.Split('.').Length == 4)
            return bcVersionArg; // an explicit 4-part version — target exactly that

        void Log(string m) => Console.Error.WriteLine($"[provision] {m}");

        if (bcVersionArg == null)
        {
            var fromEngine = ResolveDefaultProvisionVersion(bundles, Log);
            if (fromEngine != null)
                return fromEngine;
        }

        // Last resort: the engine's build version is genuinely unknown — fall back to the
        // project's app.json major (or an explicit bare-major --bc-version).
        var prefix = bcVersionArg ?? TryDeriveBcMajorFromProject(bundles);
        if (prefix == null)
        {
            Log("cannot determine which BC version to provision — pass --bc-version <ver> " +
                "(no --bc-version, no engine build info, and no readable project app.json).");
            return null;
        }
        try
        {
            var cachedDir = AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(
                AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, prefix);
            var full = Path.GetFileName(cachedDir);
            Log($"found cached BC {full} for prefix '{prefix}'.");
            return full;
        }
        catch (InvalidOperationException)
        {
            Log($"no cached BC {prefix}.x — resolving latest full version from the CDN...");
            var full = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(prefix);
            if (full == null)
                Log($"could not resolve a full BC version for prefix '{prefix}'.");
            return full;
        }
    }

    // Provisioning driver for the `provision` subcommand / --auto-provision. Resolves the
    // target BC version (explicit --bc-version, else the engine major, else the project major),
    // prefers an already-cached matching version (completing a partial one) and otherwise
    // resolves the latest full version from the CDN, then downloads the engine service-tier
    // closure if it is missing/incomplete. Returns 0 on success (already-complete counts) and
    // sets provisionedVersion to the full version to run against; 1 on failure. This is the
    // only path in the runner that downloads — on by default since issue #2024, refusable
    // with --no-auto-provision.
    //
    // <paramref name="provisionManifestApps"/> (issue #1996, AC #6): whether THIS call should
    // also provision platform-apps/test-apps. Pass true only for the `provision` subcommand,
    // which never reaches the post-SelectVersion gate in Program's top-level flow (it returns
    // immediately after this call) — for a continuing --auto-provision run, that gate is the
    // sole owner instead, so passing true there would attempt the SAME download twice in one
    // invocation (once here, pre-selection; once there, post-selection).
    internal static int RunProvisioning(string? bcVersionArg, string? artifactPathArg,
        List<string> bundles, bool provisionManifestApps, List<Action>? deferredLines, out string? provisionedVersion)
    {
        provisionedVersion = null;

        if (artifactPathArg != null)
        {
            // #2041/#2066: `deferredLines` null means print immediately (the `provision`
            // subcommand call — see the call site's comment for why); non-null means queue
            // this STEADY-STATE success line onto it instead, never an error path. The
            // caller flushes the queue once IT has confirmed no further re-exec follows —
            // see `deferredStartupLines`'s declaration in Program's top-level flow.
            if (deferredLines == null)
                Console.Error.WriteLine("[provision] --artifact-path points at an explicit dir; nothing to provision.");
            else
                deferredLines.Add(() => Console.Error.WriteLine(
                    "[provision] --artifact-path points at an explicit dir; nothing to provision."));
            return 0;
        }

        // Resolve the full version to provision.
        string? full = null;
        if (bcVersionArg != null && System.Version.TryParse(bcVersionArg, out var maybeFull) && maybeFull.Revision >= 0
            && bcVersionArg.Split('.').Length == 4)
        {
            full = bcVersionArg; // an explicit 4-part version — provision exactly that
        }
        else
        {
            // #2208: EngineMajor(AppContext.BaseDirectory) requires Ncl.dll to be physically
            // present in bin/, which is false in the ordinary shadow-copy/re-exec layout — use
            // the compile-time-baked EngineBuiltVersion() (falling back to EngineVersion, same
            // as the run path's own default selection, #2077) so this branch answers the same
            // question the same way instead of falling straight through to the project's
            // app.json, which states a floor, not the version to provision against.
            var prefix = bcVersionArg
                ?? (AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
                    ?? AlRunner.Infrastructure.BcArtifacts.EngineVersion(AppContext.BaseDirectory))?.Major.ToString()
                ?? TryDeriveBcMajorFromProject(bundles);
            if (prefix == null)
            {
                Console.Error.WriteLine("[provision] cannot determine which BC version to provision — pass " +
                    "--bc-version <ver> (no --bc-version, no engine build info, and no readable project app.json).");
                return 2; // execution error, not "test failure" — no tests ran (#2208)
            }
            // Prefer an already-cached version matching the prefix (completes a partial one);
            // otherwise resolve the latest full version from the public CDN index.
            try
            {
                var cachedDir = AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(
                    AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, prefix);
                full = Path.GetFileName(cachedDir);
                // Not gated on `quiet`: unlike the two lines below, a re-exec'd child sees a
                // DIFFERENT resolution outcome here than the parent did whenever the parent
                // itself just downloaded (parent: "no cached ... resolving from the CDN",
                // child: "found cached ... verifying completeness") — the two lines are not
                // literal duplicates of each other, so suppressing either risks hiding a
                // real state transition rather than a genuine repeat.
                Console.Error.WriteLine($"[provision] found cached BC {full} for prefix '{prefix}' — verifying completeness.");
            }
            catch (InvalidOperationException)
            {
                Console.Error.WriteLine($"[provision] no cached BC {prefix}.x — resolving latest full version from the CDN...");
                full = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(prefix);
                if (full == null)
                {
                    Console.Error.WriteLine($"[provision] could not resolve a full BC version for prefix '{prefix}'.");
                    return 2; // execution error, not "test failure" — no tests ran (#2208)
                }
            }
        }

        var serviceTierDir = AlRunner.Infrastructure.BcArtifacts.ArtifactDirFor(full);
        var report = AlRunner.Infrastructure.ProvisioningCheck.Check(full, serviceTierDir);
        if (report.Ok)
        {
            // #2041/#2066: the steady-state "nothing to do" line — deferred, same reasoning
            // as the --artifact-path branch above. AutoProvision's own download progress
            // messages below are NOT gated/deferred: they only fire once regardless (by the
            // time a shadow-re-exec child gets here the download already completed, so IT
            // takes this same Ok branch instead), and a download in progress is exactly the
            // kind of "real one-time work" .claude/rules/loud-failures.md means to stay loud.
            var fullForPrint = full;
            var serviceTierDirForPrint = serviceTierDir;
            if (deferredLines == null)
                // The `provision` subcommand's own report — this line IS its deliverable
                // (there is no test run after it to summarize instead), so it always prints.
                Console.Error.WriteLine($"[provision] BC {fullForPrint} engine artifacts already complete at {serviceTierDirForPrint}.");
            else
                // Issue #2239: on a normal continuing run, "nothing to provision" is
                // startup bookkeeping, not a result — gated behind --verbose.
                deferredLines.Add(() =>
                {
                    if (AlRunner.Log.Verbose)
                        Console.Error.WriteLine(
                            $"[provision] BC {fullForPrint} engine artifacts already complete at {serviceTierDirForPrint}.");
                });
        }
        else if (!AlRunner.Infrastructure.ProvisioningCheck.AutoProvision(full, serviceTierDir))
            return 1;

        if (provisionManifestApps)
        {
            // Issue #2103: test toolkit FIRST, platform apps second — the same ordering the
            // --auto-provision path uses, for the same reason. Whether the bundle needs the
            // platform set is decided by walking the real Microsoft dependency edges, and those
            // edges live in the test-toolkit packages' own NavxManifest.xml. Fetch that set
            // first and EnsurePlatformAppsProvisioned can read the answer instead of guessing it
            // from a hand-written table that was only ever right for one BC version.
            // #2558: fail loudly (exit 2) rather than warn-and-continue over a partial toolkit
            // — mirrors the --auto-provision path's own post-download re-check a few hundred
            // lines up (`toolkitPresent = ...; if (!toolkitPresent) { ...; return 2; }`).
            if (!EnsureTestToolkitProvisioned(full))
                return 2;
            EnsurePlatformAppsProvisioned(full, bundles);
        }
        provisionedVersion = full;
        return 0;
    }

    // Issue #1678: `provision` used to stop at the engine closure + test toolkit, so its half
    // of the "[provision-gap]" fix text ("al-runner provision") was wrong — the subcommand
    // never touched platform apps at all, and re-running it after the gap message reported the
    // engine "already complete" and exited without ever creating <artifacts>/<version>/platform-apps.
    // Detect the gap the same way the --auto-provision path does: scan the TARGET bundles'
    // own `.alpackages` (the standard shape any AL project's symbol download produces) for
    // symbol-only Microsoft platform apps, and download the R2R package set for THEIR version
    // (which can be a different minor than <paramref name="full"/>, the engine's own version —
    // see DeriveProvisionMajorMinor) into the runner-owned platform-apps dir for that version.
    // No-op when no bundle is given (`al-runner provision` with no target) or the bundle(s)
    // carry no symbol-only platform apps.
    internal static void EnsurePlatformAppsProvisioned(string engineVersion, List<string> bundles)
    {
        // Issue #1996: an empty/nonexistent bundleAlpackagesDirs used to be an early no-op here
        // (the subcommand's own copy of the "empty cache = complete" bug), and CheckPlatformApps
        // alone can never see a manifest need for an app (Application Test Library) that has no
        // service-tier DLL fallback and is therefore simply absent, not symbol-only. Consult the
        // manifest directly — same decision engine the main --auto-provision gate uses.
        var bundleAlpackagesDirs = AlRunner.Infrastructure.ProvisioningCheck.CollectBundleAlpackagesDirs(
            bundles, out var inaccessibleBundleDirs);
        // Issue #2206 — same reasoning as the main gate: skipped, but named.
        var inaccessibleWarning =
            AlRunner.Infrastructure.ProvisioningCheck.FormatInaccessibleScanWarning(inaccessibleBundleDirs);
        if (inaccessibleWarning != null) Console.WriteLine(inaccessibleWarning);
        var platformReport = AlRunner.Infrastructure.ProvisioningCheck.CheckPlatformApps(
            engineVersion, bundleAlpackagesDirs);
        var manifestDependencyRoots = ScanManifestDependencyRoots(bundles);
        // Issue #2103: include the runner-owned test-apps dir in what the decision may READ.
        // EnsureTestToolkitProvisioned has just populated it, and those packages' own manifests
        // are where the real Microsoft dependency edges come from — the per-version fact that
        // decides whether this bundle needs the platform set at all. Adding it cannot make the
        // platform set look falsely complete: Application Test Library ships in the w1
        // Extensions set, never in the test-apps set.
        var edgeSearchDirs = bundleAlpackagesDirs
            .Append(TestAppsDirFor(engineVersion))
            .ToList();
        var decision = AlRunner.Infrastructure.ProvisioningCheck.DecideManifestProvisioning(
            manifestDependencyRoots, platformReport, edgeSearchDirs);
        foreach (var badPkg in decision.UnreadablePackages)
            Console.Error.WriteLine(
                $"[provision] warning: could not read the manifest of '{badPkg}' — its Microsoft " +
                "dependency edges are unknown, so a provisioning need it implies may be missed.");
        if (!decision.ShouldDownloadPlatform)
        {
            // Issue #2073: "already present" is only true when something was actually VERIFIED
            // present. Issue #2205: and "do not need the platform R2R apps set" was equally
            // unverified — it was printed for a bundle whose implicit Microsoft roots the need
            // detection simply never looked at. The message now states what was checked and
            // what was found there; see BuildPlatformProvisionSkippedMessage.
            Console.Error.WriteLine(AlRunner.Infrastructure.ProvisioningCheck.BuildPlatformProvisionSkippedMessage(
                decision.RequiredPlatformApps, edgeSearchDirs));
            return;
        }

        // Issue #2077: always target the SELECTED engine version's own major.minor — never one
        // derived from cache contents, which used to silently redirect the download to whatever
        // minor happened to already be vendored in the bundle's own `.alpackages`.
        var mm = AlRunner.Infrastructure.ProvisioningCheck.ResolveProvisionMajorMinor(engineVersion);
        {
            var cacheMm = !platformReport.Ok
                ? AlRunner.Infrastructure.ProvisioningCheck.DeriveProvisionMajorMinor(platformReport, engineVersion)
                : AlRunner.Infrastructure.ProvisioningCheck.DerivePresentPlatformMajorMinor(bundleAlpackagesDirs, engineVersion);
            var skewNote = AlRunner.Infrastructure.ProvisioningCheck.BuildProvisionVersionSkewNote(
                mm, cacheMm,
                !platformReport.Ok
                    ? "a symbol-only platform app already in the bundle's package cache"
                    : "platform apps already in the bundle's package cache");
            if (skewNote != null)
                Console.Error.WriteLine(skewNote);
        }

        // Reuse-first, the same check the --auto-provision path in the main flow already does
        // (FindWarmProvisionedVersion) and this one never did. It did not matter while the need
        // was almost never detected here — `provision` simply printed "nothing to provision" and
        // stopped. Once issue #2205 made the need real for ordinary bundles, its absence meant
        // every single `al-runner provision` re-fetched the full 116 MB platform set over a
        // complete one already on disk. Skipped when the bundle's OWN package cache holds a
        // symbol-only platform app (issue #1678): that is a known-bad package a warm set
        // elsewhere does not repair.
        var provisionFloors = AlRunner.Infrastructure.ProvisioningCheck.DropUnsatisfiableFloors(
            AlRunner.Infrastructure.ProvisioningCheck.DetermineVersionFloors(manifestDependencyRoots), engineVersion);
        var warmVersion = platformReport.Ok
            ? FindWarmProvisionedVersion(
                AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, mm,
                decision.RequiredPlatformApps, needTest: false,
                provisionFloors, m => Console.Error.WriteLine(m))
            : null;
        if (warmVersion != null)
        {
            Console.Error.WriteLine(AlRunner.Infrastructure.ProvisioningCheck.BuildPlatformProvisionSkippedMessage(
                decision.RequiredPlatformApps,
                new[]
                {
                    AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
                        AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, warmVersion),
                }));
            return;
        }

        var platformFull = AlRunner.Provisioning.ArtifactDownloader.ResolveVersion(
            mm, m => Console.Error.WriteLine($"[provision] {m}"));
        if (platformFull == null)
        {
            Console.Error.WriteLine(
                $"[provision] warning: could not resolve a full BC artifact version for platform apps '{mm}'. " +
                $"Symbol-only Microsoft platform apps found: {string.Join(", ", platformReport.Issues.Select(i => i.Name))}.");
            return;
        }
        var platformAppsOut = AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, platformFull);
        Console.Error.WriteLine($"[provision] fetching Microsoft platform R2R apps for BC {platformFull} → {platformAppsOut}");
        try
        {
            var rc = AlRunner.Provisioning.ArtifactDownloader.PlatformApps(
                platformFull, platformAppsOut, AlRunner.Infrastructure.BcArtifacts.SelectedCountry,
                m => Console.Error.WriteLine($"[provision] {m}"));
            if (rc != 0)
                Console.Error.WriteLine(
                    $"[provision] warning: could not fetch platform apps for BC {platformFull}. " +
                    $"Test bundles calling into Base Application / System Application / Business Foundation " +
                    $"codeunits will need --package-cache <dir-with-those-apps>.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[provision] warning: platform-apps download failed: {ex.Message}");
        }
    }

    // The MS test toolkit (Any, Library Assert, Library Variable Storage, Test Runner,
    // Tests-TestLibraries, Permissions Mock, System Application Test Library) is what every
    // test bundle's app.json actually depends on, but it ships in the platform artifact rather
    // than with the engine closure. Without it a test app resolves those deps to whatever
    // symbols-only copies its own .alpackages happen to hold and dies at runtime — so the user
    // had to hand-assemble a package dir with no command that could produce one.
    // Provisioned into <artifacts>/<version>/test-apps/, which DefaultPackageCacheDirs scans,
    // so a provisioned machine needs no --package-cache for the toolkit at all.
    // #2558: thin wrapper over ProvisioningCheck.EnsureTestToolkitProvisioned — the real logic
    // lives there so it is unit-testable with a fake download delegate (no real network call).
    // Returns false (rather than only warning) on either kind of provisioning failure — a
    // download that failed outright, or one that reported success (rc == 0) without the
    // sentinel app actually landing — so RunExplicitProvisionModes can fail the whole
    // `provision --test-apps` invocation loudly instead of exiting 0 over a partial toolkit.
    internal static bool EnsureTestToolkitProvisioned(string fullVersion)
    {
        var dir = TestAppsDirFor(fullVersion);
        return AlRunner.Infrastructure.ProvisioningCheck.EnsureTestToolkitProvisioned(
            fullVersion,
            dir,
            (v, d, l) => AlRunner.Provisioning.ArtifactDownloader.TestApps(v, d, l),
            m => Console.Error.WriteLine($"[provision] {m}"));
    }

    internal static string TestAppsDirFor(string fullVersion)
        => AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, fullVersion);
}
