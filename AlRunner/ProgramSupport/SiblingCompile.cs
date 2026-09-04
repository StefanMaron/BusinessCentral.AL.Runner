namespace AlRunner;

// The layered source-dependency pre-pass: compiling sibling source apps (deps that
// are themselves AL source, not a prebuilt .app) ahead of the bundle under test, and
// the --precompile / --emit-app single-app entry points. Split out of Program.cs
// (#2665) -- purely static, no captured state.
internal static partial class ProgramSupport
{

    internal static int RunPrecompile(string[] subArgs)
    {
        string? input = null;
        string? output = null;
        var caches = new List<string>();
        for (int i = 0; i < subArgs.Length; i++)
        {
            if (subArgs[i] == "--out" && i + 1 < subArgs.Length) { output = subArgs[++i]; continue; }
            if (subArgs[i] == "--package-cache" && i + 1 < subArgs.Length) { caches.Add(subArgs[++i]); continue; }
            if (input == null) { input = subArgs[i]; continue; }
        }
        if (input == null || output == null)
        {
            Console.Error.WriteLine("Usage: Runner --precompile <input.app> --out <output.dll> [--package-cache PATH ...]");
            return 2;
        }
        var manifest = AppLoader.ReadManifest(input);
        if (manifest == null) { Console.Error.WriteLine($"Failed to read manifest from {input}"); return 2; }

        // #2131: select the BC version that matches the app being precompiled (its own
        // manifest Version — Microsoft test-apps/platform-apps are versioned identically to
        // the BC build they ship with, e.g. "Library Assert" v28.1.49838.50794 lives under
        // artifacts/28.1.49838.50794/) BEFORE computing the package-cache search dirs below.
        // Without this, DefaultPackageCacheDirs() falls back to ITS OWN lazy "latest version
        // in the artifacts cache" default (BcArtifacts.EnsureSelected), which is almost never
        // the version whose test-apps/platform-apps directories actually hold this app's
        // dependencies — exactly the "search path is too narrow" symptom #2131 reports
        // (AL1022 for System Application / Application Test Library / PEPPOL, none of which
        // exist under whatever version happened to be "latest"). Best-effort: an app whose
        // version does not correspond to any provisioned BC artifact directory (e.g. a
        // non-Microsoft or hand-versioned .app) falls through to the pre-existing lazy-default
        // behavior unchanged. A caller who already selected a version explicitly (a normal
        // bundle run reaching this helper, or a future --bc-version on --precompile) is left
        // alone — SelectVersion is call-once, so this never overrides that choice.
        if (!AlRunner.Infrastructure.BcArtifacts.IsSelected)
        {
            try { AlRunner.Infrastructure.BcArtifacts.SelectVersion(manifest.Version.ToString(), null); }
            catch
            {
                // No artifacts dir named exactly after this app's version — fall through to
                // the pre-existing lazy "latest in cache" default (triggered the first time
                // DefaultPackageCacheDirs() below reads BcArtifacts.SelectedVersion).
            }
        }

        var packageCacheDirs = caches.Count > 0 ? ExpandPackageCacheDirs(caches).ToList() : DefaultPackageCacheDirs().ToList();
        // Mirror the main bundle-run flow's runnerOwnedPlatformAppsDir/runnerOwnedTestAppsDir
        // fold-in (issue #1996) — always include the SELECTED version's own runner-owned
        // platform-apps/test-apps dirs when present on disk, even when the caller passed an
        // EXPLICIT --package-cache that doesn't happen to include them. System Application
        // (needed to compile Microsoft test-toolkit apps like Library Assert, whose own
        // NavxManifest.xml <Dependencies> is empty — the need is via the implicit `Platform=`
        // root, not an explicit dependency edge) lives in platform-apps, not test-apps.
        packageCacheDirs = AlRunner.PrecompileSupport.WidenPackageCacheDirs(
            packageCacheDirs,
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir,
            AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString());

        // #2156 (found while adding #2152's proving test for this subcommand): --precompile
        // dispatches before the main run flow's own "Cecil-rewrite Ncl.dll in place" step ever
        // runs (that block lives much further down in Main, past the `--precompile` early
        // return), so on a genuinely cold environment — nothing has yet forced a fresh Cecil
        // rewrite of the bin-directory Ncl.dll for the selected BC version — NavEnvironment's
        // real, unpatched static constructor runs for the first time inside
        // BcRuntime.ApplyAllPatches and calls WindowsIdentity.GetCurrent() unconditionally,
        // which throws PlatformNotSupportedException on Linux before a single AL object is
        // even read. Mirror the main flow's own sequence here: rewrite, then re-exec once IF
        // this rewrite was the fresh one — loading a byte-identical freshly-rewritten Ncl in
        // the SAME process that wrote it can intermittently throw BadImageFormatException (see
        // the main flow's own comment on this exact hazard), so a fresh rewrite always re-execs
        // rather than continuing in this process. AL_RUNNER_REEXECED (shared with the main
        // flow's guard) prevents an infinite loop if the child somehow rewrites again.
        //
        // #2065: "mirror the main flow's own sequence" was only half done — the main flow's
        // rewrite is unreachable until the SHADOW HOP below has already run, and this one had
        // no hop in front of it, so on an install that does not ship Ncl.dll the rewrite
        // CREATED the file in the caller's own directory (measured: a 10.7 MB
        // Microsoft.Dynamics.Nav.Ncl.dll appearing in AlRunner/bin/Release/net8.0 after one
        // `--precompile`, which then suppressed the shadow hop for every subsequent invocation
        // from that directory). Take the same hop first; the child runs from a directory that
        // legitimately holds Ncl.dll before ITS trusted-platform-assemblies list is computed,
        // and the rewrite below then replaces a file rather than creating one. See
        // TryShadowReexec's own comment for the full reasoning, and
        // AlRunner.Tests/PrecompileNclShadowHopTests.cs for the proof in both directions.
        //
        // Deliberately variantSwapDir: null — `--precompile` does not do per-BC-minor engine
        // variant selection today (the main bundle-run flow computes variantSwapDir from
        // EngineVariants long after this early dispatch), so passing null keeps this call to
        // exactly the "Ncl.dll isn't shipped" half of the decision and changes nothing else
        // about the subcommand's behaviour.
        {
            var shadowChildExit = TryShadowReexec(variantSwapDir: null);
            if (shadowChildExit.HasValue) return shadowChildExit.Value;
        }

        {
            var srcDir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
            var binNcl = Path.Combine(AppContext.BaseDirectory, "Microsoft.Dynamics.Nav.Ncl.dll");
            var didFreshRewrite = AlRunner.Infrastructure.NclCecilRewrite.RewriteInPlace(srcDir, binNcl);
            if (didFreshRewrite && Environment.GetEnvironmentVariable("AL_RUNNER_REEXECED") != "1")
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                };
                var argv = Environment.GetCommandLineArgs();
                var underDotnet = Path.GetFileNameWithoutExtension(Environment.ProcessPath!)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
                var userArgs = underDotnet ? argv : argv.Skip(1);
                foreach (var a in userArgs)
                    psi.ArgumentList.Add(a);
                psi.Environment["AL_RUNNER_REEXECED"] = "1";
                Console.Error.WriteLine("[reexec] --precompile: fresh Ncl rewrite done — re-execing for a clean load");
                using var child = System.Diagnostics.Process.Start(psi)!;
                child.WaitForExit();
                return child.ExitCode;
            }
        }

        // Apply BC patches before any BC type is touched (BcCompiler uses BC types).
        BcRuntime.EnsureApplied();

        // Resolve transitive deps of THIS app so its compile sees them as symbol refs.
        // Add the implicit Microsoft/Application + Microsoft/System roots from the
        // manifest's Application/Platform attributes — modern .app packages (incl. the
        // BC test toolkit) rely on these instead of listing BaseApp under <Dependencies>.
        // Root-level only: synthesizing transitively would cycle (Application → BaseApp
        // → Application) and the resolver throws on cycles. Mirrors the app.json path.
        var resolver = new DependencyResolver(packageCacheDirs);
        var rootDeps = manifest.Dependencies.Concat(AppLoader.ImplicitRoots(manifest)).ToList();
        var transitive = resolver.Resolve(rootDeps);
        // For apps with empty <Dependencies/> (e.g. Customizations.app), the explicit
        // dep list is empty but the AL source may still use BaseApp/System Application
        // symbols via `using` statements. Enable the all-packages fallback so the compiler
        // can resolve those symbols from the package cache dirs.
        if (transitive.Count == 0)
            BcCompiler.SetPackageCacheFallback(manifest.AppId);
        BcCompiler.SetResolvedDeps(transitive, packageCacheDirs);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var compiler = new BcCompiler();
        var assembler = new BcAssembler();

        var alSources = AppLoader.ExtractAl(input);
        if (alSources.Count == 0)
        {
            Console.Error.WriteLine($"--precompile: {input} contains no src/*.al — nothing to compile");
            return 2;
        }
        var tempDir = Path.Combine(Path.GetTempPath(), "al-runner-precompile",
            Sanitize($"{manifest.Publisher}_{manifest.Name}_{manifest.Version}"));
        Directory.CreateDirectory(tempDir);
        foreach (var existing in Directory.EnumerateFiles(tempDir, "*.al"))
        {
            try { File.Delete(existing); } catch { }
        }
        foreach (var (name, src) in alSources)
            File.WriteAllText(Path.Combine(tempDir, Sanitize(name)), src);

        BcEmitOutput emitOut;
        try
        {
            emitOut = compiler.Emit(new[] { tempDir }, manifest.Name, tempDir);
        }
        catch (Exception ex)
        {
            // Surface the full flattened emit exception so the developer sees the root cause
            // without needing BCCOMPILER_DIAG=1.
            var detail = ex is AggregateException agg
                ? string.Join("\n  ", agg.Flatten().InnerExceptions.Select(e => $"{e.GetType().Name}: {e.Message}"))
                : $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"--precompile: EMIT-FAIL for {manifest.Publisher}_{manifest.Name} v{manifest.Version}:");
            Console.Error.WriteLine($"  {detail}");
            return 3;
        }
        var emitted = emitOut.Sources;
        if (emitted.Count == 0)
        {
            // Fail LOUDLY — print the diagnostics that explain WHY 0 objects emitted
            // (binding errors and per-object emit crashes), by default. A bare
            // "EMIT-ZERO, set an env var" message is the silent-failure mode issue
            // #1620 / loud-failures.md forbids: the developer must see what broke.
            Console.Error.WriteLine($"--precompile: EMIT-ZERO — 0 of {manifest.Name}'s objects emitted ({manifest.Publisher}_{manifest.Name} v{manifest.Version})");
            var diags = emitOut.Diagnostics;
            if (diags.Count == 0)
                Console.Error.WriteLine("  Compilation.Emit() returned 0 sources with no diagnostics (set BCCOMPILER_DIAG=1 for BC-internal compiler detail).");
            else
            {
                Console.Error.WriteLine($"  {diags.Count} blocking diagnostic(s):");
                const int cap = 40;
                foreach (var d in diags.Take(cap))
                    Console.Error.WriteLine($"    {d}");
                if (diags.Count > cap)
                    Console.Error.WriteLine($"    ... and {diags.Count - cap} more");
            }
            return 3;
        }
        // AL-diagnostic compile-failure guard (#2150), extended to --precompile (#2152). Same
        // BC ContinueBuildOnError shape as the other three paths: `emitted` can come back
        // non-empty (a broken object's sibling still emitted) at the same time
        // emitOut.Diagnostics also carries an Error-severity diagnostic for the broken one — a
        // real service tier would refuse to publish this dependency app regardless. Failing
        // loudly here matters more than it might look: --precompile's whole point is producing
        // a DLL another bundle run trusts as a precompiled dependency (see
        // precompiled-dll-respect.md), so a silently-accepted compile error here would poison
        // every bundle that later depends on this output.
        if (emitOut.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine(
                $"--precompile: AL-DIAGNOSTIC-FAIL for {manifest.Publisher}_{manifest.Name} v{manifest.Version}: " +
                $"{emitted.Count} object(s) emitted but {emitOut.Diagnostics.Count} AL error(s) were " +
                $"reported by BC's own compiler; a real service tier would refuse to publish this module:");
            foreach (var d in emitOut.Diagnostics)
                Console.Error.WriteLine($"  {d}");
            return 3;
        }
        var asmName = $"Dep_{Sanitize(manifest.Publisher)}_{Sanitize(manifest.Name)}_{manifest.Version.ToString().Replace('.', '_')}";
        var compile = assembler.Compile(asmName, emitted);
        if (!compile.Success)
        {
            Console.Error.WriteLine($"--precompile: COMPILE-FAIL for {manifest.Publisher}_{manifest.Name} v{manifest.Version}:");
            foreach (var err in compile.Errors)
                Console.Error.WriteLine($"  {err.Split('\n')[0]}");
            return 3;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, compile.AssemblyBytes!);
        sw.Stop();
        Console.WriteLine(
            $"precompiled {manifest.Name} v{manifest.Version} → {output} " +
            $"({compile.AssemblyBytes!.Length} bytes, {sw.ElapsedMilliseconds}ms)");
        return 0;

        static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            return sb.ToString();
        }
    }

    // ── --emit-app subcommand ──────────────────────────────────────────────────
    // Usage: --emit-app <bundleDir> <outPath> [--package-cache PATH ...]
    // Emits the bundle dir as a real NAVX .app package using PackageModuleOutputter.
    // Useful as a standalone debug tool and as the core of the layered pre-pass.
    internal static int RunEmitApp(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: al-runner --emit-app <bundleDir> <outPath> [--package-cache PATH ...]");
            return 2;
        }
        var bundleDir = Path.GetFullPath(args[0]);
        var outPath = Path.GetFullPath(args[1]);
        var caches = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if ((args[i] == "--package-cache") && i + 1 < args.Length)
                caches.Add(args[++i]);
        }

        var appJsonPath = Path.Combine(bundleDir, "app.json");
        var identity = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath);
        if (identity == null)
        {
            Console.Error.WriteLine($"--emit-app: could not read identity from {appJsonPath}");
            return 2;
        }

        Console.WriteLine($"  [{identity.Name}] {identity.Dependencies.Count} dep(s) declared in app.json");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(
                bundleDir, identity, outPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--emit-app: EXCEPTION {ex.GetType().Name}: {ex}");
            return 3;
        }
        sw.Stop();
        var info = new FileInfo(outPath);
        Console.WriteLine($"emit-app: {identity.Name} {identity.Version} → {outPath} ({info.Length} bytes, {sw.ElapsedMilliseconds}ms)");
        return 0;
    }

    // #2669: GetDepSymbolCompiler (AlRunner.Infrastructure.DepSymbolCompilerCache) hands back the
    // SAME BcCompiler instance across repeated calls to RunLayeredPrePass/BuildSiblingSourceDeps
    // within the SAME process (--watch, --server) — see that class's own header for why.
    internal static BcCompiler GetDepSymbolCompiler(string dir) => AlRunner.Infrastructure.DepSymbolCompilerCache.GetOrCreate(dir);

    // ── Layered source build pre-pass ─────────────────────────────────────────
    // Detects inter-bundle dependencies, emits impl bundles in topo order into a
    // per-run workspace cache dir, and prepends that dir to packageCacheDirs.
    // Completely inert when bundles.Count <= 1 or no inter-bundle dep edges exist.
    internal static List<string> RunLayeredPrePass(List<string> bundles, List<string> packageCacheDirs, List<string> workspaceDirsOut)
    {
        // Read identity of every bundle.
        var identities = new Dictionary<string, AlRunner.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            var abs = Path.GetFullPath(bundle);
            // FindBucketRoot might point up; prefer direct app.json or bucket root.
            var appJson = Path.Combine(abs, "app.json");
            if (!File.Exists(appJson))
            {
                var root = FindBucketRoot(abs);
                if (root != null) appJson = Path.Combine(root, "app.json");
            }
            if (!File.Exists(appJson)) continue;
            var id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson);
            if (id != null) identities[abs] = id;
        }

        if (identities.Count < 2) return packageCacheDirs; // nothing to wire

        // Build dep edges: bundle B "depends on" bundle A if B's deps contain A's AppId
        // (or A's Name+Publisher as fallback).
        var idByKey = identities.ToDictionary(
            kv => kv.Key,
            kv => kv.Value,
            StringComparer.OrdinalIgnoreCase);

        // impls = bundles that at least one other bundle declares as a dependency.
        var implPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, id) in idByKey)
        {
            foreach (var (otherPath, otherId) in idByKey)
            {
                if (string.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase)) continue;
                bool dependsOn = otherId.Dependencies.Any(dep =>
                    (dep.AppId != Guid.Empty && dep.AppId == id.AppId) ||
                    (string.Equals(dep.Name, id.Name, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(dep.Publisher, id.Publisher, StringComparison.OrdinalIgnoreCase)));
                if (dependsOn) implPaths.Add(path);
            }
        }

        if (implPaths.Count == 0) return packageCacheDirs; // no inter-bundle deps

        // Skip any impl that already has a real, compiler-valid prebuilt .app (one with
        // a SymbolReference.json) in the package caches — e.g. RecoverySolutions ships
        // MainApps/Customizations.Test/.alpackages/Customizations.app, a symbol+source
        // package built by alc. That real .app serves BOTH compile-time symbols (via BC's
        // native .app scanner, which merges tableextensions correctly — our synthetic
        // symbols.json does NOT) AND runtime code (DependencyLoader compiles its src/*.al).
        // Synthesizing a competing .app here would only shadow the real one with weaker
        // symbols, reintroducing AL0132/AL0133 on the dependent's tableextension fields.
        foreach (var implPath in implPaths.ToList())
        {
            if (!idByKey.TryGetValue(implPath, out var implId)) continue;
            var prebuilt = packageCacheDirs
                .Where(Directory.Exists)
                .SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(d, "*.app"))
                .FirstOrDefault(f =>
                {
                    var m = AppLoader.ReadManifest(f);
                    return m != null && m.AppId == implId.AppId
                        && AppLoader.HasSymbolReference(f);
                });
            if (prebuilt != null)
            {
                // ...but only while that .app is not STALE. It is matched on AppId alone, so a
                // months-old package in a project's .alpackages would otherwise beat the source
                // directory the user passed on the command line — surfacing as a wall of bogus
                // AL0791 / AL0185 diagnostics against source that is perfectly valid, with only
                // the "[layered] ... skipping in-process synthesis" line above to explain it.
                // The verdict is on CONTENT, not mtime — see PrebuiltShadowCheck's header for why
                // mtime ordering answers a different question, and gets it wrong in both directions.
                var shadow = AlRunner.Infrastructure.PrebuiltShadowCheck.Evaluate(prebuilt, implPath);
                if (shadow.Stale)
                {
                    Console.WriteLine($"[layered] {implId.Name} {implId.Version} has a prebuilt symbol package " +
                        $"({Path.GetFileName(prebuilt)}) but it is STALE ({shadow.Reason}) — " +
                        $"synthesizing from source instead.");
                    continue; // keep implPath: build it from source
                }

                Console.WriteLine($"[layered] {implId.Name} {implId.Version} already has a prebuilt symbol package " +
                    $"({Path.GetFileName(prebuilt)}) — skipping in-process synthesis.");
                implPaths.Remove(implPath);
            }
        }
        if (implPaths.Count == 0) return packageCacheDirs; // every impl already prebuilt

        // Topological sort of impl paths (deps before dependents).
        var sortedImpls = TopologicalSort(implPaths.ToList(), idByKey);

        // Each impl gets its OWN deterministic cache dir keyed on THAT impl's own
        // sources + dependency identities. Editing one impl therefore only invalidates
        // its own dir — the unchanged siblings keep cache-HITting. (A single shared
        // combined-key dir, the previous design, orphaned every sibling's cache
        // whenever any one impl changed → a full layered rebuild on each edit.)
        // #1821: was hardcoded to ~/.cache/al-runner/workspace-deps regardless of --cache;
        // now follows the same isolation root al-out already honoured.
        var workspaceRoot = AlRunner.Infrastructure.CacheRoots.Resolve("workspace-deps");

        // Each impl dir is recorded as a synthetic-workspace dir (kept out of the
        // compile-time .app scanner — source-only .app, no SymbolReference.json →
        // AL1023) and prepended to the caches so it wins over a stale cached .app.
        var implDirs = new List<string>();
        // Every impl bundle's own .alpackages, collected so they can be added to the shared
        // caches returned to the dependent bundles. A dependent (e.g. a test bundle) resolves
        // its dep on an impl by following the impl's synthesized .app, which declares the impl's
        // OWN deps — including vendored/ISV apps (e.g. a licensing app) that live only in the
        // impl's .alpackages. Without these dirs the dependent's resolution fails with
        // "Dependency not found" for that transitive dep, so the impl never loads and its
        // namespaces read as unknown. (Compile symbols for the impl itself come from the
        // *.symbols.json sidecar; these dirs cover its transitive .app closure.)
        var implAlpackagesDirs = new List<string>();

        int emitted = 0;
        foreach (var implPath in sortedImpls)
        {
            if (!idByKey.TryGetValue(implPath, out var implId)) continue;

            // Remember the impl's SOURCE dir by AppId so NavApp.GetResource can serve its
            // app.json resourceFolders files when the impl loads as a dependency via the
            // synthesized workspace .app (which carries no /resources/ part).
            AlRunner.Patches.NavAppResourcePatches.RegisterSourceDirForApp(implId.AppId, implPath);

            // The impl bundle's own .alpackages (same dirs the main per-bundle compile scans),
            // reused for both this impl's symbol-emit and the dependent-visible caches below.
            var implBucketRootForPkgs = FindBucketRoot(implPath) ?? implPath;
            var thisImplAlpackages = AlRunner.Infrastructure.SafeDirectoryScan.Directories(implBucketRootForPkgs, ".alpackages")
                .ToList();
            foreach (var d in thisImplAlpackages)
                if (!implAlpackagesDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                    implAlpackagesDirs.Add(d);

            // The workspace dirs of the impls ALREADY built by this loop, snapshotted before
            // this impl's own dir joins the list. sortedImpls is topologically ordered, so an
            // impl that this one depends on has necessarily been written by now — and #2178 was
            // that this snapshot was never taken, so every impl resolved against the same list
            // computed before the loop and could not see any of its siblings' output. A chain
            // three apps deep (test -> middle -> base, middle and base BOTH compiled from
            // source) therefore failed on the middle app with "Dependency not found:
            // <publisher>/<base app>" — naming a package the runner had written itself one
            // line earlier. Two apps never reproduced it: the single impl in a two-app bundle
            // has no impl dependency of its own.
            var priorImplDirs = implDirs.ToList();

            var implKey = ComputeSourceWorkspaceKey(new[] { implPath }, idByKey);
            var wsDir = Path.Combine(workspaceRoot, implKey[..12]);
            Directory.CreateDirectory(wsDir);
            if (!implDirs.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
                implDirs.Add(wsDir);
            if (!workspaceDirsOut.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
                workspaceDirsOut.Add(wsDir);

            var safePublisher = Sanitize(implId.Publisher);
            var safeName = Sanitize(implId.Name);
            var safeVer = implId.Version.ToString().Replace('.', '_');
            var appFileName = $"{safePublisher}_{safeName}_{safeVer}.app";
            var outPath = Path.Combine(wsDir, appFileName);
            var symBase = Path.Combine(wsDir, $"{safePublisher}_{safeName}_{safeVer}");
            var symbolsPath = symBase + ".symbols.json";
            var depsPath = symBase + ".symbols.deps.json";
            var hadApp = File.Exists(outPath);
            var hadSymbols = File.Exists(symbolsPath) && File.Exists(depsPath);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── Step 1: compile the impl's symbols (*.symbols.json + deps sidecar) ──
            // This is the COMPILE-time half of the handoff: the dependent bundle
            // (e.g. Customizations.Test) resolves the impl's symbols from this
            // *.symbols.json via BcCompiler's chained JsonSymbolReferenceLoader (the
            // workspace dir is registered through SetExtraSymbolDirs in Main). The
            // synthetic .app emitted in Step 2 carries source only (no
            // SymbolReference.json) and serves the RUNTIME half.
            //
            // Two traps navigated here:
            // (1) Corpus hang — SetPackageCacheFallback is scoped only to this call and
            //     immediately reset with ResetPackageCacheFallback() so it never leaks
            //     into subsequent per-bundle SetResolvedDeps compiles or corpus runs.
            // (2) Self-reference (AL0275 / AL1023) — when the impl is later compiled as
            //     its OWN bundle, BcCompiler.GetSharedReferences skips any JSON spec whose
            //     AppId == _currentAppId (set per bundle) and also skips the impl's own
            //     AppId from _resolvedDeps, so the impl's own symbols are invisible to
            //     its own compile.
            if (!hadSymbols)
            {
                try
                {
                    // Resolve the impl's OWN dependency closure (declared + the implicit
                    // Application/System roots from app.json) transitively, exactly like the
                    // main per-bundle compile does. This replaces the former all-.app
                    // SetPackageCacheFallback, which scanned EVERY package in the caches —
                    // 134 apps / 353MB in the RS Extensions dir → ~215s per impl. The
                    // Application closure pulls only BaseApp / System App / Business
                    // Foundation (≈5 apps), so the symbol compile is fast and identical in
                    // coverage (an app that uses BaseApp via namespace depends, implicitly,
                    // on Application — never on the whole marketplace).
                    // ScopeCurrentAppIdentity sets _currentAppId to the impl so
                    // GetSharedReferences excludes the impl from its own specs (self-ref guard).
                    // Include the impl bundle's OWN .alpackages in the resolver + symbol dirs —
                    // the SAME dirs the main per-bundle compile uses (see the bundlePkgDirs path
                    // in the compile loop). They carry the impl's vendored/declared deps (e.g.
                    // an ISV licensing app) AND the Microsoft platform `System` app whose symbols
                    // define the System.* / System.AI.* namespaces (e.g. the "Copilot Capability"
                    // enum). Without them the layered impl symbol-emit resolves against only the
                    // global --package-cache and fails where the standalone compile succeeds:
                    // "Dependency not found" for a vendored dep, or AL0185/AL0133 "Copilot
                    // Capability is missing". The impl compiles fine on its own BECAUSE it uses
                    // these dirs; the layered impl-emit must too.
                    // ORIGINAL package cache dirs + the impl's .alpackages (NOT extendedCaches,
                    // which includes wsDir — wsDir has no valid .app yet at this point anyway).
                    var implSymbolDirs = thisImplAlpackages.Concat(packageCacheDirs).Distinct().ToList();
                    // RESOLUTION set (#2178): the compile set above, PLUS the workspace dirs of
                    // the impls already built by this loop and their .alpackages. This is what
                    // lets an impl declare a dependency on ANOTHER impl in the same invocation —
                    // the synthetic .app the earlier iteration wrote lives only in its workspace
                    // dir, which is otherwise not visible until this whole function returns
                    // extendedCaches to the per-bundle compiles below.
                    //
                    // Deliberately kept SEPARATE from implSymbolDirs, which is what
                    // SetResolvedDeps hands to BC's own .app scanner: a synthetic workspace .app
                    // carries no SymbolReference.json and makes that scanner report AL1023
                    // "package not valid" for the whole compilation. The compile-time half of an
                    // earlier impl travels through its *.symbols.json sidecar instead, via
                    // SetExtraSymbolDirs below — exactly the split the per-bundle compile in Main
                    // already uses (see the SetExtraSymbolDirs(layeredWorkspaceDirs) call sites).
                    var implResolveDirs = implSymbolDirs
                        .Concat(priorImplDirs)
                        .Concat(implAlpackagesDirs)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var implResolver = new DependencyResolver(implResolveDirs);
                    var implDeps = implResolver.Resolve(implId.Dependencies);
                    BcCompiler.SetResolvedDeps(implDeps, implSymbolDirs);
                    // AFTER SetResolvedDeps, which resets _extraSymbolDirs. Passing an empty
                    // list is not the same as not calling it at all — the previous impl's call
                    // has already been cleared by SetResolvedDeps, so this is a no-op either way
                    // and the guard is only here to avoid churning the loader signature.
                    if (priorImplDirs.Count > 0)
                        BcCompiler.SetExtraSymbolDirs(priorImplDirs);
                    // #2669: EmitDepSymbolsIncremental instead of a plain EmitDepSymbols on a
                    // throwaway `new BcCompiler()` — GetDepSymbolCompiler hands back the SAME
                    // instance this impl used last time (if any), so a re-synthesis after a small
                    // edit costs work proportional to that edit instead of the whole dependency
                    // module. See GetDepSymbolCompiler's own comment above for why keying on
                    // implPath is safe even though it's a looser identity than AppId/version.
                    using (BcCompiler.ScopeCurrentAppIdentity(implId.AppId, implId.Publisher, implId.Version))
                    {
                        GetDepSymbolCompiler(implPath).EmitDepSymbolsIncremental(
                            new[] { implPath }, implId.Name, implId.AppId, implId.Publisher, implId.Version,
                            symbolsPath, implPath, out var tookFastPath, out var fallbackReason);
                        // Same "not gated behind --verbose" reasoning as [watch]'s own FULL REBUILD
                        // line above (see this file's header comment): a full compile here is the
                        // ~22s-per-edit cost #2669 exists to eliminate, so which path was taken is a
                        // RESULT the developer needs to see, not an internal diagnostic.
                        Console.WriteLine(tookFastPath
                            ? $"[layered] {implId.Name} {implId.Version}: RAD incremental (fast path)"
                            : $"[layered] {implId.Name} {implId.Version}: full compile ({fallbackReason})");
                    }
                    // Declare the FULL compile closure — the resolved deps (real AppIds/versions)
                    // UNIONed with the Microsoft platform apps vendored in the impl's own
                    // .alpackages. Filtering to non-Optional declared deps drops the implicit
                    // platform roots (System Application, platform System, …) that carry types
                    // like "Temp Blob"/"Copilot Capability" appearing in the impl's public
                    // signatures, degrading them to __MissingTypeSymbol__ downstream. See #1546.
                    DepsSidecarWriter.Write(
                        depsPath, implId.Publisher, implId.Name, implId.Version, implId.AppId,
                        DepsSidecarWriter.BuildClosure(
                            implDeps.Select(d => new DepsSidecarWriter.DepEntry(
                                d.Manifest.Publisher, d.Manifest.Name, d.Manifest.Version, d.Manifest.AppId)),
                            ScanVendoredPlatformApps(thisImplAlpackages),
                            implId.AppId));
                }
                catch (Exception ex)
                {
                    // Loud failure per repo rule — the dependent bundle cannot compile
                    // against this impl without its symbols, so don't continue silently.
                    throw new InvalidOperationException(
                        $"[layered] Failed to emit symbols for impl '{implId.Name}' from {implPath}: {ex.Message}", ex);
                }
            }

            // ── Step 2: emit the .app — runtime/identity package ONLY, NO embedded
            // SymbolReference.json ─────────────────────────────────────────────────
            // The synthetic NAVX package we emit (8-byte header) is faithful enough for
            // our own AppLoader/DependencyResolver (identity + runtime source extraction),
            // but it is NOT a byte-valid MS NAVX package (real MS apps use a 40-byte header
            // with version + content-hash + trailing magic). Embedding SymbolReference.json
            // makes BC's *own* package reader try to load the .app as a symbol-reference
            // package, which then fails its header validation with AL1023 "package not valid".
            //
            // Compile-time symbol resolution does NOT need the embed: it is served by the
            // *.symbols.json sidecar written above (Step 1), picked up by BcCompiler's
            // chained JsonSymbolReferenceLoader over the workspace dir — exactly the
            // mechanism BuildSiblingSourceDeps uses for the (green) corpus internalsVisibleTo
            // fixture. So we pass null here and let the sidecar carry the symbols.
            if (!hadApp)
            {
                try
                {
                    AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(
                        implPath, implId, outPath, symbolReferenceJson: null);
                }
                catch (Exception ex)
                {
                    // Loud failure per repo rule — never silently continue.
                    throw new InvalidOperationException(
                        $"[layered] Failed to emit impl package '{implId.Name}' from {implPath}: {ex.Message}", ex);
                }
            }

            sw.Stop();
            var info = new FileInfo(outPath);
            var cacheVerb = hadApp && hadSymbols ? "cache HIT" : "WROTE";
            Console.WriteLine($"[layered] {cacheVerb} {implId.Name} {implId.Version} → {appFileName} (src .app + sidecar symbols, {info.Length} bytes, {sw.ElapsedMilliseconds}ms)");
            emitted++;
        }

        if (emitted > 0)
            Console.WriteLine($"[layered] pre-built {emitted} impl package(s) in-process across {implDirs.Count} cache dir(s)");

        // Impl dirs first (win over any stale cached .app), then the original caches, then the
        // impl bundles' own .alpackages (last, so they never shadow a package-cache resolution —
        // they only ADD the impls' transitive/vendored .app closure a dependent needs to resolve
        // its dep on an impl). Distinct preserves order and drops any dir already listed.
        var extendedCaches = new List<string>(implDirs);
        extendedCaches.AddRange(packageCacheDirs);
        extendedCaches.AddRange(implAlpackagesDirs);
        return extendedCaches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            return sb.ToString();
        }
    }

    // Topological sort: return items in dependency-first order.
    // Simple Kahn's algorithm over the impl subset.
    // ── Sibling source-dependency pre-pass ────────────────────────────────────
    // For a dependency declared in a bundle's app.json that has no compiled .app in
    // any package cache, look for a matching AL-source app in a sibling directory
    // (the parent of the bundle root), compile it in-process to a .app, and prepend
    // a fresh workspace cache dir so the per-bundle DependencyResolver finds it like
    // any other dep. This is what lets the corpus's two-app internalsVisibleTo
    // fixture (tests/.../al-language-internals-fixture next to tests/.../al-language)
    // resolve. Inert when no declared dep matches a sibling source app.
    internal static List<string> BuildSiblingSourceDeps(List<string> bundles, List<string> packageCacheDirs, List<string> workspaceDirsOut)
    {
        // 1. Collect each bundle's declared (non-implicit) deps + their bundle roots.
        var neededDeps = new List<DependencyRef>();
        var bundleRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            var abs = Path.GetFullPath(bundle);
            var appJson = Path.Combine(abs, "app.json");
            if (!File.Exists(appJson))
            {
                var root = FindBucketRoot(abs);
                if (root != null) appJson = Path.Combine(root, "app.json");
            }
            if (!File.Exists(appJson)) continue;
            var id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson);
            if (id == null) continue;
            bundleRoots.Add(Path.GetFullPath(Path.GetDirectoryName(appJson)!));
            // Skip Optional (implicit Microsoft Application/System) roots — those live
            // in the package caches, never as a sibling source app.
            neededDeps.AddRange(id.Dependencies.Where(d => !d.Optional));
        }
        if (neededDeps.Count == 0) return packageCacheDirs;

        // 2. Discover candidate source apps in the parent dir of each bundle root.
        var sourceApps = new Dictionary<string, AlRunner.Infrastructure.BundleIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundleRoot in bundleRoots)
        {
            var parent = Path.GetDirectoryName(bundleRoot);
            if (parent == null || !Directory.Exists(parent)) continue;
            foreach (var sub in Directory.EnumerateDirectories(parent))
            {
                var subAbs = Path.GetFullPath(sub);
                if (bundleRoots.Contains(subAbs)) continue; // not a bundle itself
                var aj = Path.Combine(subAbs, "app.json");
                if (!File.Exists(aj)) continue;
                var sid = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(aj);
                if (sid != null) sourceApps[subAbs] = sid;
            }
        }
        if (sourceApps.Count == 0) return packageCacheDirs;

        var existingPackageDirs = bundleRoots
            .SelectMany(root => AlRunner.Infrastructure.SafeDirectoryScan.Directories(root, ".alpackages"))
            .Concat(packageCacheDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 3. Match needed deps to sibling source apps (by AppId, else Name+Publisher), but
        // only when the dependency is not already available as an .app. Real projects often
        // keep a packaged copy under .alpackages; that package has authoritative symbols
        // (including tableextension field merging) while the sibling source is only needed
        // as a fallback when no package exists.
        // #2178: worklist, not a single pass over the bundles' own declared deps. A sibling
        // source app that gets BUILT here brings its own declared dependencies into scope, and
        // those may themselves only exist as sibling source apps. Matching one level deep left
        // the second level undiscovered entirely, so the chain failed inside this function's
        // own per-dep resolve below with "Dependency not found" / a provisioning-gap report for
        // an app sitting right next to the one being built. Only deps of apps we are actually
        // going to build are enqueued: a dep already satisfied by a real .app package resolves
        // through that package's own closure, exactly as before.
        var toBuild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depQueue = new Queue<DependencyRef>(neededDeps);
        var seenDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (depQueue.Count > 0)
        {
            var dep = depQueue.Dequeue();
            if (!seenDeps.Add($"{dep.AppId:N}|{dep.Publisher}/{dep.Name}")) continue;
            var packageAvailable = IsDependencyPackageAvailable(dep, existingPackageDirs);
            foreach (var (dir, sid) in sourceApps)
            {
                bool match = (dep.AppId != Guid.Empty && dep.AppId == sid.AppId) ||
                    (string.Equals(dep.Name, sid.Name, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(dep.Publisher, sid.Publisher, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
                AlRunner.Patches.RecordPatches.AddSourceDir(dir);
                // Remember the sibling source dir by AppId so NavApp.GetResource can serve
                // its resourceFolders files even when the dep loads via the synthetic
                // workspace-deps .app (which carries no /resources/ part).
                AlRunner.Patches.NavAppResourcePatches.RegisterSourceDirForApp(sid.AppId, dir);
                if (!packageAvailable)
                {
                    toBuild.Add(dir);
                    foreach (var transitive in sid.Dependencies.Where(d => !d.Optional))
                        depQueue.Enqueue(transitive);
                }
            }
        }
        if (toBuild.Count == 0) return packageCacheDirs;

        static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            return sb.ToString();
        }

        // 4. Topo-sort (deps before dependents) + compile each dep to its OWN
        // deterministic workspace dir, keyed on that dep's own sources + dep
        // identities. Editing one source dep then only invalidates its own cache —
        // unchanged sibling source deps keep cache-HITting. (A single shared
        // combined-key dir orphaned every sibling whenever any one changed.)
        var sorted = TopologicalSort(toBuild.ToList(), sourceApps);
        // #1821: was hardcoded to ~/.cache/al-runner/workspace-deps regardless of --cache;
        // now follows the same isolation root al-out already honoured.
        var workspaceRoot = AlRunner.Infrastructure.CacheRoots.Resolve("workspace-deps");
        // Synthetic-workspace dirs (per dep): source-only .apps (no SymbolReference.json)
        // + symbols.json sidecars. Kept out of the compile-time .app scanner (AL1023)
        // but used for runtime resolution + symbols.json handoff. See Main.
        var depDirs = new List<string>();
        // The dependent bundles' own `.alpackages` carry the Microsoft platform symbol
        // closure (Base Application / System Application / Business Foundation / …) as real
        // .app files committed alongside the corpus. On CI, packageCacheDirs is EMPTY
        // (artifacts live in the symbols/service-tier dirs, not bcartifacts.cache), so the
        // Base App a source-dep tableextension extends is ONLY resolvable from here. Index
        // these for the source-dep dependency resolution + symbol loader below.
        var bundleAlpackagesDirs = bundles
            .Where(Directory.Exists)
            .SelectMany(b => AlRunner.Infrastructure.SafeDirectoryScan.Directories(b, ".alpackages"))
            .Distinct()
            .ToList();
        var resolveDirs = bundleAlpackagesDirs.Concat(packageCacheDirs).Distinct().ToList();
        int emitted = 0;
        foreach (var dir in sorted)
        {
            if (!sourceApps.TryGetValue(dir, out var sid)) continue;
            // The workspace dirs of the source deps already built by this loop, snapshotted
            // before this dep's own dir joins the list. `sorted` is topological, so a dep this
            // one depends on has necessarily been written by now — see the identical snapshot
            // in RunLayeredPrePass and #2178.
            var priorDepDirs = depDirs.ToList();
            // Per-dep cache dir keyed on THIS dep's own sources + dep identities.
            var depKey = ComputeSourceWorkspaceKey(new[] { dir }, sourceApps);
            var wsDir = Path.Combine(workspaceRoot, depKey[..12]);
            Directory.CreateDirectory(wsDir);
            if (!depDirs.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
                depDirs.Add(wsDir);
            if (!workspaceDirsOut.Contains(wsDir, StringComparer.OrdinalIgnoreCase))
                workspaceDirsOut.Add(wsDir);
            // Register the source-dep's AL dir for runtime metadata parsing, so its
            // tableextensions on Base App tables (e.g. a cross-app tableextension adding a
            // field to "Item Journal Batch") get merged into the base table's NCLMetaTable.
            // Without this, runtime field lookup throws "extension field N not found in
            // NCLMetaTable". This runs before RecordPatches.Register(), so the dir is parsed
            // during Register (not immediately) — see ParseAllRegisteredSourceFiles.
            // Compile-time visibility is handled separately by the symbols.json emit below.
            AlRunner.Patches.RecordPatches.AddSourceDir(dir);
            var appFileName = $"{Sanitize(sid.Publisher)}_{Sanitize(sid.Name)}_{sid.Version.ToString().Replace('.', '_')}.app";
            var outPath = Path.Combine(wsDir, appFileName);
            var hadApp = File.Exists(outPath);
            if (!hadApp)
            {
                try
                {
                    AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(dir, sid, outPath);
                }
                catch (Exception ex)
                {
                    // Loud failure per repo rule — never silently continue.
                    throw new InvalidOperationException(
                        $"[source-dep] Failed to emit source dependency '{sid.Name}' from {dir}: {ex.Message}", ex);
                }
            }
            // Compile-visible half: emit the dep's AL symbols (*.symbols.json) + deps
            // sidecar so the DEPENDENT app can COMPILE against it. The synthetic .app
            // above carries no SymbolReference.json, so without this the dep is
            // runtime-loadable but invisible to the compiler (AL0185). BcCompiler's
            // GetSharedReferences chains a JsonSymbolReferenceLoader over the workspace
            // dir to pick these up. Revived from main's DepCompiler / SymbolJson.
            // Resolve THIS dep's own dependency closure (declared + transitive) against the
            // dependent bundles' .alpackages + packageCacheDirs, then hand it to BcCompiler —
            // exactly like RunLayeredPrePass and the main per-bundle compile. Without this, a
            // source dep that extends a Base App object (e.g. a tableextension on "Item Journal
            // Batch") cannot resolve its target → AL0247 → BC's converter NREs → crash. The
            // resolver produces CONCRETE resolved manifests (real version + path) from the
            // .alpackages closure, which is present on CI where packageCacheDirs is empty.
            // NOT the all-packages SetPackageCacheFallback (scans every .app, hangs the corpus);
            // Resolve pulls only this dep's declared closure (BaseApp / System App / …).
            // ScopeCurrentAppIdentity sets _currentAppId so GetSharedReferences excludes the dep
            // from its own specs (self-ref guard). Reset by the per-bundle SetResolvedDeps below.
            // #2178: resolve against the workspace dirs written for the source deps BUILT
            // BEFORE this one as well, so a source dep may itself depend on another source dep.
            // As in RunLayeredPrePass, those dirs are kept out of the set handed to
            // SetResolvedDeps — BC's .app scanner reports AL1023 on a synthetic .app with no
            // SymbolReference.json — and reach the compiler as *.symbols.json through
            // SetExtraSymbolDirs instead.
            var depResolveDirs = resolveDirs
                .Concat(priorDepDirs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var depResolver = new DependencyResolver(depResolveDirs);
            var resolvedDepDeps = depResolver.Resolve(sid.Dependencies);
            BcCompiler.SetResolvedDeps(resolvedDepDeps, resolveDirs);
            // AFTER SetResolvedDeps, which resets _extraSymbolDirs.
            if (priorDepDirs.Count > 0)
                BcCompiler.SetExtraSymbolDirs(priorDepDirs);
            var symBase = Path.Combine(wsDir, $"{Sanitize(sid.Publisher)}_{Sanitize(sid.Name)}_{sid.Version.ToString().Replace('.', '_')}");
            var symbolsPath = symBase + ".symbols.json";
            var depsPath = symBase + ".symbols.deps.json";
            var hadSymbols = File.Exists(symbolsPath) && File.Exists(depsPath);
            if (!hadSymbols)
            {
                try
                {
                    // #2669: same GetDepSymbolCompiler + EmitDepSymbolsIncremental swap as
                    // RunLayeredPrePass above, and for the identical reason — this function follows
                    // the exact same "new BcCompiler() every call" shape for the same kind of source
                    // dependency, just discovered via a sibling directory instead of a declared impl.
                    using (BcCompiler.ScopeCurrentAppIdentity(sid.AppId, sid.Publisher, sid.Version))
                    {
                        GetDepSymbolCompiler(dir).EmitDepSymbolsIncremental(
                            new[] { dir }, sid.Name, sid.AppId, sid.Publisher, sid.Version,
                            symbolsPath, dir, out var tookFastPath, out var fallbackReason);
                        Console.WriteLine(tookFastPath
                            ? $"[source-dep] {sid.Name} {sid.Version}: RAD incremental (fast path)"
                            : $"[source-dep] {sid.Name} {sid.Version}: full compile ({fallbackReason})");
                    }
                    // Full compile closure (resolved deps ∪ vendored platform apps) — see the
                    // impl-bundle site above and #1546. Filtering to non-Optional declared deps
                    // would drop the implicit platform roots whose types appear in this dep's
                    // public surface, yielding __MissingTypeSymbol__ in the dependent compile.
                    var depOwnAlpackages = AlRunner.Infrastructure.SafeDirectoryScan.Directories(
                        FindBucketRoot(dir) ?? dir, ".alpackages");
                    DepsSidecarWriter.Write(
                        depsPath, sid.Publisher, sid.Name, sid.Version, sid.AppId,
                        DepsSidecarWriter.BuildClosure(
                            resolvedDepDeps.Select(d => new DepsSidecarWriter.DepEntry(
                                d.Manifest.Publisher, d.Manifest.Name, d.Manifest.Version, d.Manifest.AppId)),
                            ScanVendoredPlatformApps(depOwnAlpackages),
                            sid.AppId));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"[source-dep] Failed to emit symbols for '{sid.Name}' from {dir}: {ex.Message}", ex);
                }
            }

            var info = new FileInfo(outPath);
            var cacheVerb = hadApp && hadSymbols ? "cache HIT" : "WROTE";
            Console.WriteLine($"[source-dep] {cacheVerb} {sid.Name} {sid.Version} → {appFileName} (+symbols, {info.Length} bytes)");
            emitted++;
        }
        if (emitted == 0) return packageCacheDirs;
        var extended = new List<string>(depDirs);
        extended.AddRange(packageCacheDirs);
        return extended;
    }

    internal static bool IsDependencyPackageAvailable(DependencyRef dep, IReadOnlyList<string> packageDirs)
    {
        foreach (var dir in packageDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.app"))
            {
                var manifest = AlRunner.AppLoader.ReadManifest(file);
                if (manifest == null || manifest.Version < dep.Version)
                    continue;
                var idMatches = dep.AppId != Guid.Empty && dep.AppId == manifest.AppId;
                var nameMatches = string.Equals(dep.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(dep.Publisher, manifest.Publisher, StringComparison.OrdinalIgnoreCase);
                if (idMatches || nameMatches)
                    return true;
            }
        }

        return false;
    }

    internal static string ComputeSourceWorkspaceKey(
        IReadOnlyList<string> sortedDirs,
        IReadOnlyDictionary<string, AlRunner.Infrastructure.BundleIdentity> sourceApps)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var ms = new MemoryStream();
        void WriteLine(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\n");
            ms.Write(bytes, 0, bytes.Length);
        }

        // v2 (issue #1815): runner fingerprint switched from mtime+length to a content hash
        // (mtime moved on every CI rebuild, so a persisted cache could never hit), and an
        // explicit bc:<version> line was added (a content hash alone is identical across
        // every BC-version CI leg building the same commit, so without it all legs would
        // collide on one cache entry). v1 entries carried neither and must not be served.
        WriteLine("schema:v2");
        AlRunner.Infrastructure.RunnerFingerprint.WriteKeyLines(WriteLine);

        foreach (var dir in sortedDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!sourceApps.TryGetValue(dir, out var id)) continue;
            WriteLine($"app:{id.AppId}:{id.Publisher}:{id.Name}:{id.Version}");
            foreach (var dep in id.Dependencies.OrderBy(d => $"{d.Publisher}/{d.Name}/{d.Version}/{d.AppId}", StringComparer.OrdinalIgnoreCase))
                WriteLine($"dep:{dep.AppId}:{dep.Publisher}:{dep.Name}:{dep.Version}");
            var files = AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al")
                .Append(Path.Combine(dir, "app.json"))
                .Where(File.Exists)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                using var fs = File.OpenRead(file);
                WriteLine($"file:{Path.GetRelativePath(dir, file)}:{Convert.ToHexString(sha.ComputeHash(fs))}");
            }
        }

        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
    }

    internal static List<string> TopologicalSort(
        List<string> implPaths,
        Dictionary<string, AlRunner.Infrastructure.BundleIdentity> idByKey)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string path)
        {
            if (!visited.Add(path)) return;
            if (!idByKey.TryGetValue(path, out var id)) return;
            // Visit impl dependencies first.
            foreach (var dep in id.Dependencies)
            {
                var depImpl = implPaths.FirstOrDefault(p =>
                {
                    if (!idByKey.TryGetValue(p, out var pid)) return false;
                    return (dep.AppId != Guid.Empty && dep.AppId == pid.AppId) ||
                           (string.Equals(dep.Name, pid.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(dep.Publisher, pid.Publisher, StringComparison.OrdinalIgnoreCase));
                });
                if (depImpl != null) Visit(depImpl);
            }
            result.Add(path);
        }

        foreach (var p in implPaths) Visit(p);
        return result;
    }

    // Walks up from <bundlePath> until it finds a dir containing app.json.
    // Returns null if none found before /tests/ or filesystem root.
    /// <summary>
    /// Write a <c>*.symbols.json</c> (plus its dependency sidecar) for every app in this bundle
    /// that another app in the SAME bundle depends on, and register the directory holding them
    /// with the compiler so those later apps can reference them.
    ///
    /// <paramref name="appGroups"/> must already be in topological order — the symbols for a
    /// dep-of-a-dep have to exist before the app that needs them is compiled. Only genuine
    /// sibling-dependency targets are emitted: each one costs an extra compile, and a bundle
    /// with no in-bundle dependencies (the corpus, every single-app bundle) does no work at all.
    ///
    /// Failures are loud but non-fatal: without the symbols the dependent app fails to compile
    /// with AL0185, which is exactly the state this fixes, and the emit-retry already reports
    /// the dropped objects.
    ///
    /// <paramref name="bundleResolvedDeps"/> is this bundle's resolved Microsoft-platform
    /// dependency closure (the same set every suite under a parent-of-many-apps bundle
    /// implicitly gets — Base Application, System Application, …). It is recorded in each
    /// sibling's *.symbols.deps.json sidecar so BC's ReferenceManager can see that the
    /// sibling itself depends on (e.g.) Base Application. Without it a sibling whose only
    /// AL is a `tableextension ... extends <PlatformTable>` gets an EMPTY sidecar — its
    /// declaring module has no recorded path to the module owning the base table, so the
    /// extension never attaches: the base table's own fields resolve fine (they come from
    /// the primary compile's own direct reference to the platform .app) but the extension
    /// field does not, surfacing as AL0132 "'Record X' does not contain a definition for
    /// '<field>'" in the app that consumes it. A sibling that only extends another sibling's
    /// OWN table (the common case in this bundle) never depended on this fix — its base
    /// table lives in the SAME symbols.json as the extension, so no cross-module link was
    /// needed. See #1686.
    /// </summary>
    internal static void EmitSiblingSymbols(
        List<AlRunner.AppGroup> appGroups, string bundleAbs,
        IReadOnlyList<(AlRunner.AppManifest Manifest, string AppPath)> bundleResolvedDeps)
    {
        BcCompiler.SetSiblingSymbolsDir(null);
        // Not a dictionary: two suites in the same tree can (and in tests/runner-extras do)
        // carry the same app id, which would throw on insert. Only membership matters here.
        var presentIds = appGroups.Where(g => g.AppId != null).Select(g => g.AppId!.Value).ToHashSet();
        var targets = appGroups
            .SelectMany(g => g.DependsOn)
            .Where(presentIds.Contains)
            .ToHashSet();
        if (targets.Count == 0) return;

        // Per (bundle, process), not per bundle leaf name — see
        // Infrastructure/SiblingSymbolsDirectory.cs (#2586). The recursive delete below is only
        // safe because of that: it can now only ever remove THIS process's own directory, where
        // before two concurrent runners over same-leaf-named bundles deleted each other's symbols
        // mid-compile. Nobody else cleans up a private directory, so prune old ones first.
        AlRunner.Infrastructure.SiblingSymbolsDirectory.PruneStale(TimeSpan.FromDays(1));
        var dir = AlRunner.Infrastructure.SiblingSymbolsDirectory.ForBundle(bundleAbs);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [sibling-symbols] cannot prepare {dir}: {ex.Message}");
            return;
        }

        BcCompiler.SetSiblingSymbolsDir(dir);
        // A sibling's symbol compile inherits the BUNDLE-WIDE dep spec list, which in a
        // parent-of-many-apps bundle includes packages only ONE suite declares — among them
        // synthetic source-only .apps that the compiler's .app scanner cannot read at all.
        // Unlike the primary emit, this compile checks declaration diagnostics, so an unrelated
        // suite's fixture package would fail every sibling here. See ScopeSymbolBearingDepsOnly.
        using var depScope = BcCompiler.ScopeSymbolBearingDepsOnly();
        foreach (var group in appGroups)
        {
            if (group.AppId == null || !targets.Contains(group.AppId.Value)) continue;
            var symbolsPath = Path.Combine(dir, $"{group.AppId:N}.symbols.json");
            try
            {
                using (BcCompiler.ScopeCurrentAppIdentity(
                           group.AppId.Value, group.Publisher ?? "AlRunner",
                           group.Version ?? new Version(1, 0, 0, 0)))
                    new BcCompiler().EmitDepSymbols(
                        group.Paths, group.ModuleName, group.AppId.Value,
                        group.Publisher ?? "AlRunner", group.Version ?? new Version(1, 0, 0, 0),
                        symbolsPath, group.SuiteDir);
                // The dependency closure this app compiled against, so BC's ReferenceManager can
                // link types from it that appear in the sibling's public surface — same reason as
                // the source-dep sidecar (#1546); without it those types are __MissingTypeSymbol__.
                //
                // Include the BUNDLE-WIDE Microsoft-platform closure (bundleResolvedDeps) — every
                // suite under a parent-of-many-apps bundle implicitly compiles against it, this
                // sibling included. Without it, a sibling whose only AL is a `tableextension ...
                // extends <PlatformTable>` records an empty dependency closure: its declaring
                // module has no path to the module owning the base table, so the extension never
                // attaches downstream (AL0132 in the consuming app) even though the sibling's own
                // symbols.json genuinely contains the TableExtension entry. See #1686.
                DepsSidecarWriter.Write(
                    Path.Combine(dir, $"{group.AppId:N}.symbols.deps.json"),
                    group.Publisher ?? "AlRunner", group.ModuleName,
                    group.Version ?? new Version(1, 0, 0, 0), group.AppId.Value,
                    DepsSidecarWriter.BuildClosure(
                        bundleResolvedDeps.Select(d => new DepsSidecarWriter.DepEntry(
                            d.Manifest.Publisher, d.Manifest.Name, d.Manifest.Version, d.Manifest.AppId)),
                        ScanVendoredPlatformApps(
                            AlRunner.Infrastructure.SafeDirectoryScan.Directories(bundleAbs, ".alpackages")),
                        group.AppId.Value));
                // Re-index in place so the NEXT app in this loop — and every app group compiled
                // below — sees it, without rebuilding the expensive shared reference loader.
                BcCompiler.RefreshSiblingSymbols();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"  [sibling-symbols] {group.ModuleName}: {ex.GetType().Name}: {ex.Message} — " +
                    "apps depending on it will fail to compile against it");
            }
        }
    }
}
