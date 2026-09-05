namespace AlRunner;

// Reading app.json dependency declarations, bundle manifests, and building the
// AppGroup/suite structures the compile pipeline iterates. Split out of Program.cs
// (#2665) -- purely static, no captured state.
internal static partial class ProgramSupport
{

    /// <summary>
    /// The manifests whose dependency lists together define this bundle's compile closure.
    ///
    /// Normally exactly one: the bucket root's own app.json. But a PARENT directory holding
    /// many sibling apps — tests/runner-extras is 25 of them — has no app.json of its own, and
    /// FindBucketRoot walks UP looking for one, so it finds nothing. Before this, the entire
    /// dep-resolution block was gated on that single file existing, so for such a bundle
    /// SetResolvedDeps was never called and NO module got the Microsoft platform symbol
    /// closure: `Table "Field"`, `Table "Payment Method"`, `Codeunit "Library - No. Series"`
    /// and every platform enum resolved to nothing. The emit-retry then dropped each offending
    /// test codeunit as "broken", so 25 suites yielded 9 tests — while each suite run
    /// STANDALONE passed, because then FindBucketRoot landed on a directory that does have an
    /// app.json. Union the children instead: their manifests are where the `application` /
    /// `platform` roots are declared.
    /// </summary>
    internal static List<string> CollectBundleManifests(string? bucketRoot, string bundleAbs)
    {
        if (bucketRoot != null && File.Exists(Path.Combine(bucketRoot, "app.json")))
            return new List<string> { Path.Combine(bucketRoot, "app.json") };
        if (!Directory.Exists(bundleAbs)) return new List<string>();
        // Direct children only — that is the shape EnumerateSuites recognises, and it keeps
        // the scan away from app.json files buried inside extracted .app packages.
        //
        // Suites declaring a newer BC than the one under test are dropped HERE, before their
        // dependencies join the union. The union is bundle-wide, so one such suite's unmet
        // Microsoft dependency aborts the entire bundle — every sibling included — before a
        // single test runs. Filtering at BuildAppGroups alone is far too late: the run never
        // reaches it. See BcFloorGate.
        //
        // Deliberately NOT applied to the bucket-root branch above: a root manifest speaks for
        // the whole bucket, so honoring a floor there would silently skip everything under it.
        // That case should stay a loud failure.
        var children = Directory.EnumerateDirectories(bundleAbs)
            .Select(d => Path.Combine(d, "app.json"))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var kept = new List<string>();
        foreach (var m in children)
        {
            if (AlRunner.BcFloorGate.DeclaresNewerBcThanRunning(m, out var floor) && floor != null)
            {
                AlRunner.BcFloorGate.ReportSkip(m, AlRunner.BcFloorGate.SuiteNameOf(m), floor);
                continue;
            }
            kept.Add(m);
        }
        return kept;
    }

    /// <summary>
    /// Issue #1996: the manifest-driven provisioning pre-scan. Unlike <see
    /// cref="ReadBundleDependencyRoots"/> (used for the REAL dependency-resolution closure),
    /// this is deliberately per-manifest fault-tolerant — a malformed/non-object app.json is a
    /// PRE-SCAN MISS here (logged, skipped), never a crash: the normal bundle loader reaches
    /// the same file moments later and owns the real diagnostic for it (acceptance criterion
    /// #9). Returns every Microsoft dependency root across all target <paramref name="bundles"/>
    /// (not deduped/sibling-filtered — <see cref="AlRunner.Infrastructure.ProvisioningCheck.DetermineManifestNeeds"/>
    /// only cares whether ANY root names a known app, so dedup buys nothing here).
    /// </summary>
    internal static List<DependencyRef> ScanManifestDependencyRoots(List<string> bundles)
    {
        var allRoots = new List<DependencyRef>();
        foreach (var bundle in bundles)
        {
            List<string> manifests;
            try
            {
                var abs = Path.GetFullPath(bundle);
                var bucketRoot = FindBucketRoot(abs);
                manifests = CollectBundleManifests(bucketRoot, abs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[provision] manifest pre-scan: skipping '{bundle}': {ex.Message}");
                continue;
            }
            var roots = AlRunner.Infrastructure.ProvisioningCheck.TryReadManifestDependencyRoots(
                manifests, ReadDependencies, m => Console.Error.WriteLine(m));
            allRoots.AddRange(roots);
        }
        return allRoots;
    }

    /// <summary>
    /// Union the dependency roots declared across <paramref name="manifests"/>, keeping the
    /// highest version when two manifests name the same dependency.
    ///
    /// Sibling apps that are THEMSELVES part of this bundle are dropped: BuildAppGroups already
    /// emits them in topological order, so they must not also be resolved from a package cache —
    /// they have no .app there and a non-optional root that cannot be found fails the bundle.
    /// </summary>
    internal static List<DependencyRef> ReadBundleDependencyRoots(IReadOnlyList<string> manifests)
    {
        var siblingIds = new HashSet<Guid>();
        if (manifests.Count > 1)
            foreach (var m in manifests)
            {
                var id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(m);
                if (id != null) siblingIds.Add(id.AppId);
            }

        var byKey = new Dictionary<(string, string), DependencyRef>();
        foreach (var m in manifests)
            foreach (var d in ReadDependencies(m))
            {
                if (d.AppId != Guid.Empty && siblingIds.Contains(d.AppId)) continue;
                var key = (d.Name ?? string.Empty, d.Publisher ?? string.Empty);
                if (!byKey.TryGetValue(key, out var cur) || d.Version > cur.Version)
                    byKey[key] = d;
            }
        return byKey.Values.ToList();
    }

    internal static void SetBundleInfoFromAppJson(string appJsonPath)
    {
        // Remember (or clear) the bundle dir for NavApp.GetResource: the emitted test
        // assembly's resources are the files under this dir's app.json resourceFolders.
        AlRunner.Patches.NavAppResourcePatches.SetCurrentBundleDir(
            File.Exists(appJsonPath) ? Path.GetDirectoryName(Path.GetFullPath(appJsonPath)) : null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
            var root = doc.RootElement;
            var idStr = root.TryGetProperty("id", out var pid) ? pid.GetString() : null;
            var name = root.TryGetProperty("name", out var pn) ? pn.GetString() ?? "Unknown" : "Unknown";
            var pub = root.TryGetProperty("publisher", out var pp) ? pp.GetString() ?? "Unknown" : "Unknown";
            var ver = root.TryGetProperty("version", out var pv) ? pv.GetString() ?? "1.0.0.0" : "1.0.0.0";
            Guid appId = Guid.Empty;
            if (!string.IsNullOrEmpty(idStr)) Guid.TryParse(idStr, out appId);
            AlRunner.BcRuntime.SetCurrentBundleInfo(appId, name, pub, ver);
        }
        catch { /* non-fatal */ }
    }

    internal static IEnumerable<DependencyRef> ReadDependencies(string appJsonPath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
        var root = doc.RootElement;

        // Explicit deps from the `dependencies` array (third-party + any first-party
        // apps the author chose to list).
        if (root.TryGetProperty("dependencies", out var deps)
            && deps.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            int depIndex = 0;
            foreach (var d in deps.EnumerateArray())
            {
                // #2560: a `dependencies` entry with a non-string field (e.g. `"name": 123`,
                // a plain typo a hand-edited app.json can carry) used to call .GetString()
                // unguarded, raising InvalidOperationException out of the resolution path —
                // TryReadManifestDependencyRoots's pre-scan catches that (its own catch-all
                // around a DIFFERENT reader call), but ReadDependencies itself is also called
                // directly (ScanManifestDependencyRoots, the per-suite dep-resolve path), with
                // no such wrapper, so the exception was unhandled there. TryGetDepString names
                // the file, the entry's position, and the property instead of either crashing
                // OR silently `continue`-ing past the whole entry (which would just as
                // silently drop a real dependency edge) — one malformed field degrades to its
                // own default, the rest of the entry is still read and yielded normally.
                var idStr = TryGetDepString(d, "id", appJsonPath, depIndex);
                var name = TryGetDepString(d, "name", appJsonPath, depIndex) ?? "";
                var pub = TryGetDepString(d, "publisher", appJsonPath, depIndex) ?? "";
                var ver = TryGetDepString(d, "version", appJsonPath, depIndex) ?? "0.0.0.0";
                depIndex++;
                Guid id = Guid.Empty;
                if (!string.IsNullOrEmpty(idStr)) Guid.TryParse(idStr, out id);
                if (!Version.TryParse(ver, out var v)) v = new Version(0, 0, 0, 0);
                // Microsoft platform apps (Base Application / System Application / Business
                // Foundation / Application / System) are provided by the precompiled
                // service-tier DLLs at runtime and by the bundle's .alpackages symbols at
                // compile time — never loaded from a resolved .app. Some corpus/ISV manifests
                // list them EXPLICITLY (others rely on the implicit application/platform roots).
                // Mark the explicit ones Optional so resolution skips them when they aren't a
                // findable .app (e.g. on CI, where packageCacheDirs is empty) instead of failing
                // the whole bundle — matching how the implicit roots below are already Optional.
                bool isMsPlatform = AlRunner.DependencyResolver.IsMicrosoftPlatformApp(name, pub);
                // #2560: an entry whose id AND name both degraded to nothing (malformed fields
                // — see TryGetDepString above) names no findable package at all; the loud
                // warning already printed for the field is the whole diagnostic value this
                // entry can offer. Requiring resolution to find "a package named ''" turns one
                // malformed field into a hard failure for the WHOLE bundle over information we
                // already know is unusable, which is worse than the drop the issue itself says
                // to avoid — it still fails LOUDLY (the resolver's normal missing-dependency
                // message, just naming nothing useful) rather than either silently vanishing or
                // silently succeeding, but a genuinely unresolvable phantom entry blocking every
                // OTHER valid dependency in the same manifest is not a fix worth shipping.
                bool isUnresolvable = id == Guid.Empty && string.IsNullOrEmpty(name);
                yield return new DependencyRef(id, name, pub, v, Optional: isMsPlatform || isUnresolvable);
            }
        }

        // Implicit first-party deps. Modern AL apps do NOT list the Microsoft apps
        // in `dependencies`; the real `al` compiler injects them from the manifest's
        // `application` and `platform` fields. Synthesize the same roots here so they
        // resolve from the package cache, otherwise every `using Microsoft.*` is an
        // unknown namespace. The `application` umbrella app transitively pulls Base
        // Application + System Application + Business Foundation; `platform` is the
        // System (platform symbols) app. TryFind matches by (Name, Publisher) —
        // version is informational only, so an exact runtime match isn't required.
        foreach (var (field, implName) in new[] { ("application", "Application"), ("platform", "System") })
        {
            if (root.TryGetProperty(field, out var fv)
                && fv.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(fv.GetString()))
            {
                if (!Version.TryParse(fv.GetString(), out var iv)) iv = new Version(0, 0, 0, 0);
                yield return new DependencyRef(Guid.Empty, implName, "Microsoft", iv, Optional: true);
            }
        }
    }

    // #2560: reads one `dependencies[]` entry's string-typed property, tolerating a
    // non-string value (e.g. a numeric `"name": 123` in a hand-edited/malformed app.json)
    // instead of letting JsonElement.GetString() throw InvalidOperationException. Prints a
    // diagnostic naming the manifest file, the entry's zero-based position, and the property
    // so a malformed manifest is visible rather than either crashing the whole resolution
    // path or silently dropping the entry — see ReadDependencies' own call site comment.
    internal static string? TryGetDepString(System.Text.Json.JsonElement dep, string propertyName, string appJsonPath, int depIndex)
    {
        if (!dep.TryGetProperty(propertyName, out var v)) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        Console.Error.WriteLine(
            $"[provision] warning: '{appJsonPath}' dependencies[{depIndex}].{propertyName} is not a " +
            $"string (found {v.ValueKind}) — ignoring that field for this dependency entry.");
        return null;
    }

    // Collect this single suite's src/test/app* dirs for emit. Per-suite isolation
    // avoids the cross-suite object-id collisions that silently zeroed-out bundled emit.
    // When a bucket root is supplied, also include `<bucketRoot>/_shared/` so AL
    // files at the bucket level (e.g. an Assert.Codeunit.al that satisfies a
    // dependency without a runtime DLL) compile into every suite.
    /// <summary>
    /// Groups the enumerated suites into one AppGroup per app.json, ordered so that an
    /// app comes after every sibling it depends on (a sibling's symbols must exist
    /// before the app referencing it compiles). Suites without an app.json cannot carry
    /// an identity of their own and are merged into one fallback module named after the
    /// bundle, which is the pre-existing behaviour for that shape.
    /// </summary>
    internal static List<AlRunner.AppGroup> BuildAppGroups(List<string> suites, string? bucketRoot, string bundleAbs)
    {
        var groups = new List<AlRunner.AppGroup>();
        var identified = new List<(AlRunner.AppGroup Group, Guid Id)>();
        var orphanPaths = new List<string>();
        var skippedForBcFloor = new List<(string Name, Version Floor)>();

        foreach (var suite in suites)
        {
            var paths = CollectSuitePaths(suite, bucketRoot);
            var appJson = Path.Combine(suite, "app.json");
            var id = File.Exists(appJson)
                ? AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson)
                : null;
            if (id == null) { orphanPaths.AddRange(paths); continue; }

            // Honor a declared minimum BC version — see BcFloorGate. CollectBundleManifests already
            // drops these before the dependency union, which is the filter that actually keeps the
            // bundle alive; this one covers the paths that reach here with a different suite set
            // (a single suite passed as the target, --watch re-enumeration), so the two must agree.
            // BcFloorGate reports each suite once, so the overlap does not double-print.
            if (AlRunner.BcFloorGate.DeclaresNewerBcThanRunning(appJson, out var floor) && floor != null)
            {
                AlRunner.BcFloorGate.ReportSkip(appJson, id.Name, floor);
                skippedForBcFloor.Add((id.Name, floor));
                continue;
            }

            var group = new AlRunner.AppGroup(
                ModuleName: id.Name,
                AppId: id.AppId,
                Publisher: id.Publisher,
                Version: id.Version,
                Paths: paths,
                DependsOn: id.Dependencies.Select(d => d.AppId).ToList(),
                SuiteDir: Path.GetFullPath(suite));
            identified.Add((group, id.AppId));
        }

        // Topological order over sibling dependencies only. Dependencies on apps outside
        // this bundle are resolved from the package cache as before and are ignored here.
        var siblingIds = identified.Select(t => t.Id).ToHashSet();
        var emitted = new HashSet<Guid>();
        var remaining = new List<(AlRunner.AppGroup Group, Guid Id)>(identified);
        while (remaining.Count > 0)
        {
            // Take every app whose sibling dependencies are already emitted. If none
            // qualify the graph has a cycle — emit the rest in declaration order rather
            // than looping forever; BC will report the unresolved reference loudly.
            var ready = remaining
                .Where(t => t.Group.DependsOn.All(d => !siblingIds.Contains(d) || emitted.Contains(d)))
                .ToList();
            if (ready.Count == 0) ready = remaining.ToList();
            foreach (var t in ready)
            {
                groups.Add(t.Group);
                emitted.Add(t.Id);
                remaining.Remove(t);
            }
        }

        if (orphanPaths.Count > 0)
            groups.Add(new AlRunner.AppGroup(
                ModuleName: $"V2_{Path.GetFileName(bundleAbs)}",
                AppId: null, Publisher: null, Version: null,
                Paths: orphanPaths.Distinct().ToList(),
                DependsOn: Array.Empty<Guid>(),
                SuiteDir: Path.GetFullPath(bundleAbs)));

        // Restate the skips as one line after the per-suite detail. A reader scanning the tail of a
        // green run must be able to see that the run covered less than the tree contains — a skip
        // that only appears 200 lines up is a skip nobody notices.
        if (skippedForBcFloor.Count > 0)
            Console.WriteLine(
                $"  [skip] {skippedForBcFloor.Count} suite(s) need a newer BC than "
                + $"{AlRunner.Infrastructure.BcArtifacts.SelectedVersion}: "
                + string.Join(", ", skippedForBcFloor.Select(s => $"{s.Name} (>= {s.Floor})")));

        return groups;
    }

    internal static List<string> CollectSuitePaths(string suite, string? bucketRoot = null)
    {
        var all = new List<string>();
        var s = Path.Combine(suite, "src");
        var t = Path.Combine(suite, "test");
        if (Directory.Exists(s)) all.Add(s);
        foreach (var app in Directory.EnumerateDirectories(suite, "app*"))
            all.Add(app);
        if (Directory.Exists(t)) all.Add(t);
        // Flat bundle: if neither src/ nor test/ exist, include the suite root so
        // the emitter can recurse into it and find all .al files.
        if (all.Count == 0 && AlRunner.Infrastructure.SafeDirectoryScan.Files(suite, "*.al").Any())
            all.Add(suite);
        if (bucketRoot != null)
        {
            var shared = Path.Combine(bucketRoot, "_shared");
            if (Directory.Exists(shared)) all.Add(shared);
        }
        return all;
    }

    // Deterministic cache key for the bundled-mode emit:
    //   sha256( runner-asm-mtime-ticks
    //         | moduleName
    //         | each (ordered dep id+version)
    //         | each (.al file relpath + sha256-of-contents) sorted )
    // Hashed in a single pass with line-separated framing so two different file
    // layouts can't collide. The key is hex-encoded sha256 (64 chars).
    internal static string ComputeAlCacheKey(
        IReadOnlyList<string> alFolders,
        string moduleName,
        IReadOnlyList<string> ordered,
        string? appRootDir = null)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var ms = new MemoryStream();
        void WriteLine(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\n");
            ms.Write(bytes, 0, bytes.Length);
        }

        // 0. Cache schema version — bumped whenever the on-disk cache layout
        //    (sidecar set, sidecar shape, or hash framing) changes. Old DLLs
        //    written before the bump simply hash to a different key and become
        //    unreachable garbage in <cacheDir>; the new key MISSes and rebuilds.
        //    v2: added <key>.enum-registry.json sidecar so cache HIT replays the
        //    AlEnumMetadataRegistry side-effects that emit would have set up.
        //    v3: enum sidecar includes interface implementation codeunit ids.
        //    v4: sidecar also carries the AlReportMetadataRegistry (per-report
        //        runtime metadata XML) so cache HIT replays real report metadata.
        //    v5: sidecar also carries the AlReportLayoutRegistry (per-report
        //        `rendering { layout(...) }` declarations) so cache HIT replays the
        //        rows behind the Report Layout List virtual table (2000000234).
        //    v6: sidecar also carries the AlPageMetadataRegistry (per-page runtime
        //        metadata XML) so cache HIT replays the real page control tree that
        //        NCLMetaForm.LoadMetadata() builds from it.
        //    v7: report-layout rows carry IsDefault (the report's DefaultRenderingLayout),
        //        without which a cache HIT could not resolve a multi-layout report's
        //        default-layout render and hydrated nothing.
        //    v8: sidecar also carries the AlXmlPortMetadataRegistry (per-xmlport runtime
        //        metadata XML) so cache HIT replays the real node schema that
        //        NCLMetaXmlPort.LoadMetadata() builds from it.
        //    v9: enum sidecar entries carry per-value Captions (issue #1775 —
        //        Format(<enum value>) must return the declared Caption, not the member
        //        name). A v8 sidecar deserialises with Captions null, which
        //        AlEnumOptionMetadata already treats as "no captions captured" — silently
        //        wrong for a cache HIT, not a cache miss, without this bump.
        //    v10: runner fingerprint switched from mtime+length to a content hash of the
        //        runner assembly (issue #1815 finding 2 — every CI leg rebuilds the runner,
        //        so mtime moved on every run and a persisted cache could never hit), and an
        //        explicit bc:<version> line was added (finding 3 — a content hash is
        //        IDENTICAL across every BC-version leg building the same commit; without an
        //        explicit version line all 8 legs would collide on one cache entry and a leg
        //        could load AL output compiled against another BC version's symbols). v9
        //        entries carried neither and must not be served under the new key shape.
        //    v11: added a manifest fragment (preprocessorSymbols/features/contextSensitiveHelpUrl
        //        read from the app's own app.json — see BcCompiler.ReadManifestCompilerInputs).
        //        #1943: before this, editing app.json changed neither the AL source bytes nor
        //        the CLI --define set, so the key was IDENTICAL before and after — a cache HIT
        //        would silently serve the DLL compiled under the OLD manifest values (wrong #if
        //        branch, missing NoImplicitWith, stale contextSensitiveHelpUrl) forever, until
        //        something else in the key happened to change. v10 entries never hashed the
        //        manifest at all and must not be served under the new key shape.
        //    v12 (issue #1997): added a tdd:<0|1> line. --tdd keeps recovered sources for
        //        objects a normal run drops entirely and can (in a follow-up) inject
        //        generated members into the in-memory compile — a --tdd assembly and a
        //        normal-mode assembly for the SAME source bytes are not the same output.
        //        Without this line a bare run and a --tdd run over identical sources hash
        //        identically, and whichever compiled first would silently serve the other:
        //        a normal run reusing a --tdd-generated DLL, or (just as bad) a later --tdd
        //        run reusing a normal-mode DLL and reporting the excluded tests' vanished
        //        instead of failed. v11 entries never hashed this and must not be served.
        WriteLine("schema:v12");
        WriteLine($"tdd:{(AlRunner.BcCompiler.IsTddMode() ? "1" : "0")}");

        // 1. Runner assembly fingerprint (content hash, not mtime — see v10 note above) +
        //    the selected BC version, so any rewriter/polyfill/patch change in the runner,
        //    or running against a different BC version, forces a cache miss.
        AlRunner.Infrastructure.RunnerFingerprint.WriteKeyLines(WriteLine);

        WriteLine($"module:{moduleName}");

        // 2. Preprocessor symbols from --define / --preprocessor-symbols. They select which
        //    #if branch compiles, so two runs over byte-identical sources but different symbol
        //    sets are different compilations. Omitting them made --define a silent no-op on any
        //    cache hit: a bare run and a --define run produced the same key, and whichever
        //    compiled first served the other. Written unconditionally so the line always frames
        //    the key (existing entries hash differently once and rebuild).
        WriteLine($"defines:{string.Join(",", AlRunner.BcCompiler.GetExtraPreprocessorSymbols())}");

        // 3. The app's OWN manifest properties that feed ParseOptions/CompilationOptions —
        //    preprocessorSymbols, features, contextSensitiveHelpUrl (#1943; see v11 note
        //    above). appRootDir is the directory holding app.json — the same one Emit()
        //    itself reads from (BcCompiler.ReadManifestCompilerInputs) — so an edit to any of
        //    these three properties changes this line and forces a MISS.
        WriteLine($"manifest:{AlRunner.BcCompiler.ReadManifestCacheKeyFragment(appRootDir)}");

        foreach (var d in ordered) WriteLine($"dep:{d}");

        // Enumerate every .al file in stable order, hash each. The key uses paths
        // relative to the common source root, not absolute paths, so invoking the
        // same bundle from a different current directory does not force a rebuild.
        var alFiles = alFolders
            .Where(Directory.Exists)
            .SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(Path.GetFullPath(d), "*.al"))
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var commonRoot = CommonDirectory(alFiles);
        foreach (var f in alFiles)
        {
            byte[] hash;
            using (var fs = File.OpenRead(f))
                hash = sha.ComputeHash(fs);
            var rel = commonRoot == null ? Path.GetFileName(f) : Path.GetRelativePath(commonRoot, f);
            WriteLine($"al:{rel}:{Convert.ToHexString(hash)}");
        }

        ms.Position = 0;
        var keyBytes = sha.ComputeHash(ms);
        return Convert.ToHexString(keyBytes).ToLowerInvariant();
    }

    internal static string? CommonDirectory(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return null;
        var common = Path.GetDirectoryName(Path.GetFullPath(files[0]));
        if (common == null) return null;
        foreach (var file in files.Skip(1))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            while (dir != null && common != null
                && !dir.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dir, common, StringComparison.OrdinalIgnoreCase))
            {
                common = Path.GetDirectoryName(common);
            }
            if (common == null) return null;
        }
        return common;
    }

    // Read app.json deps and feed them through DependencyResolver so the cache key
    // reflects the exact resolved set (id+version), not just declared roots. This
    // matches what BcCompiler.SetResolvedDeps fed into the compile.
    //
    // The dirs MUST match the ones the compile resolves against — bundlePkgDirs (the
    // bundle's own .alpackages, found by recursive search) CONCAT the package caches. This
    // used to be handed only the package caches, and the omission was total, not partial:
    // a bundle whose roots live in its own .alpackages could not resolve at all, the throw
    // landed in the catch below, and the key got NO dep line whatsoever. So the key was
    // blind to the entire dependency closure. Observed: adding a System.app package changed
    // the emitted DLL (3175424 -> 3206144 bytes) while the key stayed
    // 67c4f8c4622a928aae07bf1857af515bb37fc5df4ac16eb047855f5dd2f9bba8 — a warm cache then
    // serves a DLL compiled against a different dependency closure. Same defect family as
    // the --define symbols that were missing from this key.
    /// <summary>
    /// The name of the first dependency <paramref name="appRootDir"/>'s own app.json declares
    /// whose AppId is in <paramref name="changedAppIds"/>, or null when it declares none of them.
    ///
    /// <para>#2683. Used under <c>--watch</c> to decide whether a bundle may replay its previous
    /// generated C#: a bundle whose own AL files are byte-identical still cannot, if a bundle it
    /// depends on was re-synthesised this cycle, because the replayed output was compiled against
    /// that dependency's previous surface.</para>
    ///
    /// <para>Matched on AppId only. Unlike RunLayeredPrePass, which also falls back to
    /// Name+Publisher so a bundle without a declared id is still recognisable as somebody's
    /// dependency, the ids here come from that same pre-pass and are therefore always real —
    /// and the answer feeds a "may I take the fast path" decision, where a false NEGATIVE is
    /// the dangerous direction. A dependency that the pre-pass matched by name and this method
    /// cannot match by id would be one whose AppId is empty, which the pre-pass never adds.</para>
    ///
    /// <para>An unreadable or absent app.json answers null: a bundle whose manifest cannot be
    /// read has no declared dependencies to speak of, and its own compile will fail on that
    /// separately and loudly.</para>
    /// </summary>
    internal static string? DeclaredDependencyOn(string? appRootDir, IReadOnlyCollection<Guid> changedAppIds)
    {
        if (appRootDir == null || changedAppIds.Count == 0) return null;
        var appJson = Path.Combine(appRootDir, "app.json");
        if (!File.Exists(appJson)) return null;
        AlRunner.Infrastructure.BundleIdentity? id;
        try { id = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJson); }
        catch { return null; }
        if (id == null) return null;
        foreach (var dep in id.Dependencies)
            if (dep.AppId != Guid.Empty && changedAppIds.Contains(dep.AppId))
                return string.IsNullOrEmpty(dep.Name) ? dep.AppId.ToString() : dep.Name;
        return null;
    }

    internal static IReadOnlyList<string> GetOrderedDepIds(
        string? bucketRoot, IReadOnlyList<string> packageCacheDirs, string? bundleAbs = null)
    {
        // Same closure the emit actually compiles against — a parent-of-many-apps bundle has no
        // app.json of its own and takes the union of its children (see CollectBundleManifests).
        // Keying on a DIFFERENT closure than the one used to compile would let two bundles that
        // resolve differently share a cache entry.
        var depRootDir = bucketRoot ?? bundleAbs;
        if (depRootDir == null) return Array.Empty<string>();
        var manifests = CollectBundleManifests(bucketRoot, bundleAbs ?? depRootDir);
        if (manifests.Count == 0) return Array.Empty<string>();
        try
        {
            var roots = ReadBundleDependencyRoots(manifests);
            var bundlePkgDirs = AlRunner.Infrastructure.SafeDirectoryScan.Directories(depRootDir, ".alpackages")
                .ToList();
            var resolver = new AlRunner.DependencyResolver(
                bundlePkgDirs.Concat(packageCacheDirs).Distinct().ToList());
            var ordered = resolver.Resolve(roots);
            return ordered
                // Id:Version alone is NOT a content identity: a sibling source app keeps
                // its app.json version while its schema evolves during development, so a
                // key without the winning .app's file stamp served the test bundle a
                // stale compiled assembly after e.g. a field removal — a runtime
                // NavNCLFieldNotFoundException where a fresh compile correctly fails.
                // mtime+size (not a content hash) keeps big platform .apps cheap to
                // stamp; the layered pre-pass only rewrites a synthesized sibling .app
                // when its source actually changed, so stamps are stable across runs.
                .Select(d =>
                {
                    string stamp = "?";
                    try
                    {
                        var fi = new FileInfo(d.AppPath);
                        if (fi.Exists) stamp = $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
                    }
                    catch { /* unreadable path keys as "?" and simply cannot HIT */ }
                    return $"{d.Manifest.AppId:N}:{d.Manifest.Version}:{stamp}";
                })
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            // Never collapse to "no deps": an empty list is indistinguishable from a bundle
            // that genuinely has none, so two different closures would share a key and the
            // cache would hand back the wrong DLL. Fold the failure itself into the key
            // instead — an unresolvable closure is its own cache identity, and it changes
            // again as soon as the reason changes.
            Console.Error.WriteLine(
                $"  [cache] dependency resolution failed while computing the cache key for " +
                $"{depRootDir}: {ex.GetType().Name}: {ex.Message}. Keying on the failure so this " +
                $"bundle cannot share a cache entry with a resolvable one.");
            return new[] { $"unresolved:{ex.GetType().Name}:{ex.Message}" };
        }
    }
}

internal static partial class ProgramSupport
{
    /// <summary>
    /// Sum the totals of JUnit files handed in by --merge-counts: results from earlier attempts
    /// of this same run, before a watchdog abort forced a resume (#2280). An unreadable file
    /// contributes zero rather than throwing — the run's own verdict must not depend on a
    /// carry-forward file, and JUnitCounts already treats a missing/truncated file that way.
    /// </summary>
    public static AlRunner.Reporter.CarriedTotals CarriedFromEarlierAttempts(IEnumerable<string> files)
    {
        var total = default(AlRunner.Reporter.CarriedTotals);
        foreach (var f in files)
        {
            var t = AlRunner.Infrastructure.JUnitCounts.Read(f);
            total += new AlRunner.Reporter.CarriedTotals(
                (int)t.Tests, (int)(t.Tests - t.Failures - t.Errors - t.Skipped), (int)t.Failures, (int)t.Errors);
        }
        return total;
    }
}
