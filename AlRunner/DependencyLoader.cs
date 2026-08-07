// DependencyLoader — turns a topo-sorted dep list into loaded Assemblies in
// the default ALC. Three-tier resolution per dep:
//
//   Tier 1: pre-compiled DLL at <bucketRoot>/.deps-bin/<Publisher>_<Name>_<Version>.dll
//   Tier 2: R2R `.app` (publishedartifacts/*.dll) — Microsoft-shipped binaries
//   Tier 3: source-only `.app` — extract src/*.al, run BcCompiler.Emit + BcAssembler.Compile
//
// All loads cache by AppId in a process-wide dictionary so cross-bucket sharing
// is free. A `Default.Resolving` handler is installed once at first use so the
// .NET runtime can re-resolve assemblies-by-name back to the byte[]-loaded
// instances (Assembly.Load(byte[]) puts the assembly in the default ALC, but
// reference resolution still goes by name).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using AlRunner.Infrastructure;

namespace AlRunner;

public sealed class DependencyLoader
{
    private static readonly ConcurrentDictionary<Guid, Assembly> _cache = new();
    private static readonly ConcurrentDictionary<string, Assembly> _byName =
        new(StringComparer.OrdinalIgnoreCase);
    private static int _resolverInstalled;

    private readonly BcCompiler _compiler;
    private readonly BcAssembler _assembler;

    public DependencyLoader(BcCompiler compiler, BcAssembler assembler)
    {
        _compiler = compiler;
        _assembler = assembler;
        EnsureResolverInstalled();
    }

    public IReadOnlyList<Assembly> LoadAll(
        IReadOnlyList<(AppManifest Manifest, string AppPath)> ordered,
        string bucketRoot)
    {
        var list = new List<Assembly>();
        foreach (var (m, path) in ordered)
        {
            if (_cache.TryGetValue(m.AppId, out var existing))
            {
                list.Add(existing);
                continue;
            }
            // A source-only Microsoft app carries compile-time symbols, not a runtime DLL.
            // Two sub-cases, and we can only tell them apart by trying to compile:
            //   (a) Platform symbol-stub apps (System, Base Application, …) ship AL whose
            //       procedures are external/native — their REAL runtime lives in the
            //       extracted service-tier DLLs. Emit/Roslyn cannot reconstruct a body, so a
            //       Tier-3 source-compile fails; the faithful resolution is lazy dispatch from
            //       the service-tier DLL index (CodeunitPatches.FindCodeunitType / ServiceTierDllIndex).
            //   (b) Test-library apps (Tests-TestLibraries → CU131352 "Library - Document
            //       Approvals", System Application Test Library, …) ship FULL codeunit bodies
            //       NOT present in the service-tier DLLs. Those MUST source-compile so the test
            //       exercises their real bodies instead of a lying NoOp.
            // The old code unconditionally skipped ALL Microsoft source-only apps, which forced
            // case (b) into a NoOp. We now let them flow through LoadOne (whose Tier-2.5 DLL-first
            // step already short-circuits case (a) when the index covers every codeunit). If a
            // Microsoft app still reaches Tier-3 and its compile fails, that is case (a) with an
            // index gap — fall back to skip (lazy DLL dispatch), which is faithful for platform
            // apps, rather than aborting the whole run. Non-Microsoft deps keep failing LOUD.
            bool microsoftSourceOnly =
                string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
                && !AppLoader.IsR2R(path);

            // Known platform runtime apps (System Application / Base Application / Business
            // Foundation) MUST be R2R packages — their procedure bodies are external/native, so
            // Tier-3 source-compile ALWAYS fails with EMIT-ZERO. Skip it entirely and print a
            // loud, actionable provisioning-gap message instead of the cryptic "EMIT-ZERO" error.
            // The runner still functions via service-tier DLL dispatch for these codeunits.
            if (microsoftSourceOnly && AlRunner.Infrastructure.ProvisioningCheck.IsKnownPlatformRuntimeApp(m.Name))
            {
                var bcVer = AlRunner.Infrastructure.BcArtifacts.SelectedVersion.ToString();
                Console.Error.WriteLine(
                    AlRunner.Infrastructure.ProvisioningCheck.BuildPlatformAppMissingR2RMessage(
                        m.Publisher, m.Name, m.Version.ToString(), path, bcVer));
                continue;
            }

            // The platform symbols app "System" is known Tier-3-uncompilable: its bodies are
            // external/native, Roslyn always fails on the emitted `_Internal` platform calls,
            // and the failure path below then defers to service-tier DLL dispatch anyway.
            // Short-circuit to the SAME outcome without paying the doomed Emit+Roslyn pass
            // (~14.5s per bundle, measured on Pageworks 2026-07-23). One clear line replaces
            // the CS-error wall. Faithful per loud-failures: nothing new is silenced — the
            // observable end state (no assembly, lazy service-tier dispatch) is unchanged.
            if (microsoftSourceOnly
                && AlRunner.Infrastructure.ProvisioningCheck.IsPlatformSymbolOnlySystemApp(m.AppId, m.Publisher, m.Name))
            {
                Console.Error.WriteLine(
                    $"[deps] platform symbol app {m.Publisher}_{m.Name} v{m.Version}: known " +
                    $"Tier-3-uncompilable (external/native bodies) — skipping source compile, " +
                    $"deferring to service-tier DLL dispatch at runtime");
                continue;
            }

            Assembly? asm;
            try
            {
                asm = LoadOne(m, path, bucketRoot);
            }
            catch (AlRunner.Infrastructure.DependencyLoadException) when (microsoftSourceOnly)
            {
                // Platform symbol-stub app whose runtime is the service-tier DLLs: skip the
                // source-compile and let runtime codeunit resolution serve real bodies from the
                // DLL index (or NoOp for the documented system/test-toolkit ranges).
                Console.Error.WriteLine(
                    $"[deps] Microsoft source-only {m.Publisher}_{m.Name} v{m.Version}: Tier-3 compile " +
                    $"unavailable — deferring to service-tier DLL dispatch at runtime");
                continue;
            }
            if (asm != null)
            {
                _cache[m.AppId] = asm;
                _byName[asm.GetName().Name ?? ""] = asm;
                // Register app metadata so AlCallStackCapture can decorate frames.
                AlCallStackCapture.RegisterAssemblyInfo(asm, m.Name, m.Publisher, m.Version.ToString());
                // Register the assembly's app package so NavApp.GetResource can serve
                // this app's packaged resources (.app /resources/ part, or the sibling
                // source dir for source deps packaged into a synthetic workspace .app).
                AlRunner.Patches.NavAppResourcePatches.RegisterDependencyAssembly(
                    asm, m.AppId, m.Name, m.Publisher, m.Version.ToString(), path);
                // Per-assembly module identity: this dep's code sees ITS OWN app info
                // from NavApp.GetCurrentModuleInfo (real BC's executing-module rule),
                // not the bundle's.
                BcRuntime.RegisterModuleInfoForAssembly(
                    asm, m.AppId, m.Name, m.Publisher, m.Version.ToString());
                list.Add(asm);
            }
        }
        return list;
    }

    /// <summary>
    /// Every <c>&lt;root&gt;/**/.deps-bin/&lt;fileName&gt;</c>, ordered so the result is stable.
    /// Scoped to <c>.deps-bin</c> directories so this cannot pick up an unrelated DLL that
    /// happens to share the name. Returns empty on any IO error rather than throwing —
    /// a failed probe just means the lower dependency tiers take over, as before.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, string fileName)
    {
        IEnumerable<string> depsBinDirs;
        try { depsBinDirs = Directory.EnumerateDirectories(root, ".deps-bin", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var dir in depsBinDirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    private Assembly? LoadOne(AppManifest m, string appPath, string bucketRoot)
    {
        // Tier 1: precompiled DLL.
        var fileName = SanitizeFileName($"{m.Publisher}_{m.Name}_{m.Version}.dll");
        var precompiled = Path.Combine(bucketRoot, ".deps-bin", fileName);
        if (!File.Exists(precompiled))
        {
            // A bundle that is a PARENT of many apps has no .deps-bin of its own — the one
            // that matters belongs to the suite that declares the dependency, one level
            // down. Without this the Tier-1 DLL was simply not found when the same suite ran
            // as part of the parent bundle (it loads fine standalone, where bucketRoot IS
            // the suite), and the dep silently fell through to a lower tier — for a
            // source-less fixture .app that means its objects never exist at all.
            precompiled = SafeEnumerateFiles(bucketRoot, fileName).FirstOrDefault() ?? precompiled;
        }
        if (File.Exists(precompiled))
        {
            try
            {
                var bytes = File.ReadAllBytes(precompiled);
                return Assembly.Load(bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[deps] tier-1 load failed for {m.Name}: {ex.Message}");
            }
        }

        // Tier 2: R2R extract. Microsoft ships large apps (notably Base
        // Application — 5 DLL chunks) as multiple `publishedartifacts/*.dll`
        // entries. Load every DLL; the chunk that defines the user-visible
        // app type (e.g. `Codeunit9015` for "Application System Constants")
        // is not necessarily the first one. We return the chunk whose
        // assembly name matches the manifest's app name when present, else
        // the first chunk; all chunks are registered in the by-name cache so
        // the Resolving handler can serve cross-chunk references.
        if (AppLoader.IsR2R(appPath))
        {
            var dlls = AppLoader.ExtractAllDlls(appPath);
            if (dlls.Count > 0)
            {
                Assembly? primary = null;
                int loaded = 0;
                foreach (var dll in dlls)
                {
                    try
                    {
                        var asm = Assembly.Load(dll);
                        var n = asm.GetName().Name ?? "";
                        _byName[n] = asm;
                        primary ??= asm;
                        loaded++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[deps] tier-2 R2R chunk load failed for {m.Name}: {ex.Message}");
                    }
                }
                if (loaded > 1)
                    Console.Error.WriteLine($"[deps] tier-2 R2R: {m.Name} loaded {loaded} DLL chunk(s)");
                return primary;
            }
        }

        // Tier 3: source-only compile-on-the-fly.
        var sw = Stopwatch.StartNew();
        var alSources = AppLoader.ExtractAl(appPath);

        // Tier 2.5 (DLL-first): Microsoft ships its test toolkit symbol-only (AL source,
        // no compiled code). The same objects are precompiled in the extracted service-tier
        // DLL cache. If the cache covers this dep's codeunits, skip the expensive whole-app
        // source compile and let CodeunitPatches.FindCodeunitType resolve each codeunit body
        // lazily from the cache at dispatch (runs the REAL Microsoft code). Per the chosen
        // policy: source-compile only remains the fallback for objects the cache lacks.
        if (alSources.Count > 0 && ServiceTierDllIndex.Available)
        {
            var codeunitIds = ExtractCodeunitTypeNames(alSources);
            if (codeunitIds.Count > 0 && codeunitIds.All(ServiceTierDllIndex.Contains))
            {
                Console.Error.WriteLine(
                    $"[deps] DLL-first: {m.Publisher}_{m.Name} v{m.Version} — {codeunitIds.Count} codeunit(s) " +
                    $"served from extracted service-tier DLLs; skipping source compile");
                return null; // lazy dispatch via ServiceTierDllIndex
            }
        }

        if (alSources.Count == 0)
        {
            // Symbol-only package (no runtime code in this .app — normal for Microsoft
            // platform apps that are provided via service-tier DLLs loaded elsewhere).
            Console.Error.WriteLine(
                $"[deps] NOTE: {m.Publisher}_{m.Name} v{m.Version} is symbol-only " +
                $"(no runtime code in package); relying on service-tier/already-loaded assembly");
            return null;
        }

        var cacheKey = ComputeSourceDependencyCacheKey(m, appPath);
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "al-runner", "compiled-deps");
        var cachedDll = Path.Combine(cacheDir, cacheKey + ".dll");
        var reportSidecar = Path.Combine(cacheDir, cacheKey + ".report-metadata.json");
        // Sibling sidecar for the dep's `rendering { layout(...) }` declarations —
        // without it a cache HIT would leave the Report Layout List virtual table
        // (2000000234) empty for this dep's reports and layout-by-name selection
        // would fail only on warm runs.
        var reportLayoutSidecar = Path.Combine(cacheDir, cacheKey + ".report-layouts.json");
        // Same story for this dep's page metadata XML: without the sidecar a cache HIT
        // leaves every page of this dep a control-less NCLMetaForm skeleton, so TestPage
        // access to its page-variable-bound controls fails only on warm runs.
        var pageMetadataSidecar = Path.Combine(cacheDir, cacheKey + ".page-metadata.json");
        var xmlPortMetadataSidecar = Path.Combine(cacheDir, cacheKey + ".xmlport-metadata.json");
        if (File.Exists(cachedDll))
        {
            try
            {
                var cachedBytes = File.ReadAllBytes(cachedDll);
                // Replay this dep's report metadata (real MetaReport XML) so
                // SaveAs/Run of a dependency report executes on genuine metadata.
                int replayedReports = 0;
                if (File.Exists(reportSidecar))
                    replayedReports = AlReportMetadataRegistry.LoadSidecar(reportSidecar);
                if (File.Exists(reportLayoutSidecar))
                    AlReportLayoutRegistry.LoadSidecar(reportLayoutSidecar);
                if (File.Exists(pageMetadataSidecar))
                    AlPageMetadataRegistry.LoadSidecar(pageMetadataSidecar);
                if (File.Exists(xmlPortMetadataSidecar))
                    AlXmlPortMetadataRegistry.LoadSidecar(xmlPortMetadataSidecar);
                Console.Error.WriteLine(
                    $"[deps] source-cache HIT: {m.Name} v{m.Version} key={cacheKey[..12]} ({cachedBytes.Length} bytes, {replayedReports} report-metadata entries)");
                return Assembly.Load(cachedBytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[deps] source-cache read/load failed for {m.Name}: {ex.Message}; rebuilding");
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(),
            "al-runner-deps", SanitizeFileName($"{m.Publisher}_{m.Name}_{m.Version}"));
        Directory.CreateDirectory(tempDir);
        // Clean previously emitted .al files so a stale one doesn't pollute the compile.
        foreach (var existing in Directory.EnumerateFiles(tempDir, "*.al"))
        {
            try { File.Delete(existing); } catch { }
        }
        foreach (var (name, src) in alSources)
        {
            var fileSafe = SanitizeFileName(name);
            File.WriteAllText(Path.Combine(tempDir, fileSafe), src);
        }
        // Stage report layout resources (.rdlc/.docx/.xlsx) next to the .al so a code-bearing
        // report's `LayoutFile = './X.rdlc'` reference resolves at compile time. Without these,
        // BC's layout-embed step NREs (AL1081) and — because Emit is atomic per module — zeroes
        // the WHOLE app, taking otherwise-clean codeunits (e.g. CU131352) down with it. Written
        // with their real (URL-decoded) names so the relative './<Name>' reference matches.
        foreach (var existing in Directory.EnumerateFiles(tempDir, "*.rdlc")
                     .Concat(Directory.EnumerateFiles(tempDir, "*.docx"))
                     .Concat(Directory.EnumerateFiles(tempDir, "*.xlsx")))
        {
            try { File.Delete(existing); } catch { }
        }
        foreach (var (layoutName, layoutBytes) in AppLoader.ExtractReportLayouts(appPath))
        {
            try { File.WriteAllBytes(Path.Combine(tempDir, Path.GetFileName(layoutName)), layoutBytes); }
            catch (Exception ex) { Console.Error.WriteLine($"[deps] layout stage failed for {layoutName}: {ex.Message}"); }
        }

        IReadOnlyList<EmittedSource> emitted;
        // Snapshot the report-metadata registry before this dep's emit so we can
        // persist exactly the entries THIS app contributed to its own sidecar.
        var reportIdsBeforeEmit = new HashSet<int>(AlReportMetadataRegistry.Ids);
        var pageIdsBeforeEmit = new HashSet<int>(AlPageMetadataRegistry.Ids);
        var xmlPortIdsBeforeEmit = new HashSet<int>(AlXmlPortMetadataRegistry.Ids);
        // Scope _currentAppId to the dep's own identity for the duration of this compile.
        // GetSharedReferences uses _currentAppId to exclude the "current app" from its
        // reference specs. Without this, the dep's resolved spec (from _resolvedDeps of
        // the PARENT bundle) would be both in the reference list AND in the primary AL
        // source → AL0275 "ambiguous reference". The scope is restored on dispose.
        try { using (BcCompiler.ScopeCurrentAppIdentity(m.AppId, m.Publisher, m.Version))
                  emitted = _compiler.Emit(new[] { tempDir }, m.Name).Sources; }
        catch (Exception ex)
        {
            // EMIT-FAIL: the BC Compilation.Emit() call threw (e.g. "Unexpected value 'None'
            // of type NavTypeKind", "Index was outside the bounds", etc.).
            // Do NOT swallow — this dependency is broken and running without it will produce
            // cryptic failures (NavNCLMissingMethodException with object ID 0).
            var detail = DependencyLoadException.FlattenException(ex);
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: EMIT-FAIL — {detail}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "EMIT-FAIL", detail, ex);
        }
        if (emitted.Count == 0)
        {
            // EMIT-ZERO: Emit returned success but produced no sources — BC's silent
            // zero-output sentinel. The dependency has source but nothing was compiled.
            const string detail =
                "BC Compilation.Emit() returned 0 sources from app AL source " +
                "(silent zero-output sentinel — likely a NavTypeKind/emitter crash swallowed internally). " +
                "Run with BCCOMPILER_DIAG=1 or --precompile for full diagnostics.";
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: EMIT-ZERO — {detail}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "EMIT-ZERO", detail);
        }

        var asmName = $"Dep_{SanitizeIdent(m.Publisher)}_{SanitizeIdent(m.Name)}_{m.Version.ToString().Replace('.', '_')}";
        var compile = _assembler.Compile(asmName, emitted);
        if (!compile.Success)
        {
            // COMPILE-FAIL: Roslyn failed to compile the C# polyfill bodies BC emitted.
            var allErrors = string.Join(" | ", compile.Errors.Select(e => e.Split('\n')[0]));
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: COMPILE-FAIL — {allErrors}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "COMPILE-FAIL", allErrors);
        }

        sw.Stop();
        Console.Error.WriteLine(
            $"[deps] compiled-on-the-fly: {m.Name} v{m.Version} ({sw.ElapsedMilliseconds}ms). " +
            $"For faster CI, run --precompile to snapshot.");
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(cachedDll, compile.AssemblyBytes!);
            // Persist the report metadata THIS app's emit contributed, so cache
            // HIT replays it (mirrors the bundle enum-registry sidecar).
            var ownReportIds = AlReportMetadataRegistry.Ids
                .Where(i => !reportIdsBeforeEmit.Contains(i)).ToArray();
            int sidecarCount = AlReportMetadataRegistry.SaveSidecar(reportSidecar, ownReportIds);
            AlReportLayoutRegistry.SaveSidecar(reportLayoutSidecar, ownReportIds);
            AlPageMetadataRegistry.SaveSidecar(pageMetadataSidecar,
                AlPageMetadataRegistry.Ids.Where(i => !pageIdsBeforeEmit.Contains(i)).ToArray());
            AlXmlPortMetadataRegistry.SaveSidecar(xmlPortMetadataSidecar,
                AlXmlPortMetadataRegistry.Ids.Where(i => !xmlPortIdsBeforeEmit.Contains(i)).ToArray());
            Console.Error.WriteLine(
                $"[deps] source-cache WROTE: {m.Name} v{m.Version} key={cacheKey[..12]} ({compile.AssemblyBytes!.Length} bytes, {sidecarCount} report-metadata entries)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[deps] source-cache write failed for {m.Name}: {ex.Message}");
        }
        try { return Assembly.Load(compile.AssemblyBytes!); }
        catch (Exception ex)
        {
            // LOAD-FAIL: the compiled bytes could not be loaded into the ALC.
            var detail = DependencyLoadException.FlattenException(ex);
            Console.Error.WriteLine($"[dep-load-fail] {m.Publisher}_{m.Name} v{m.Version}: LOAD-FAIL — {detail}");
            throw new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), "LOAD-FAIL", detail, ex);
        }
    }

    private static string ComputeSourceDependencyCacheKey(AppManifest manifest, string appPath)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        void WriteLine(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\n");
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteLine("schema:v1");
        var runnerLoc = typeof(BcAssembler).Assembly.Location;
        if (!string.IsNullOrEmpty(runnerLoc) && File.Exists(runnerLoc))
            WriteLine($"runner:{File.GetLastWriteTimeUtc(runnerLoc).Ticks}:{new FileInfo(runnerLoc).Length}");
        else
            WriteLine("runner:unknown");
        WriteLine($"app:{manifest.AppId}:{manifest.Publisher}:{manifest.Name}:{manifest.Version}");
        foreach (var dep in manifest.Dependencies.OrderBy(d => $"{d.Publisher}/{d.Name}/{d.Version}/{d.AppId}", StringComparer.OrdinalIgnoreCase))
            WriteLine($"dep:{dep.AppId}:{dep.Publisher}:{dep.Name}:{dep.Version}");
        using (var fs = File.OpenRead(appPath))
            WriteLine($"app-bytes:{Convert.ToHexString(sha.ComputeHash(fs))}");

        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
    }

    // Cheap source scan for "codeunit <id> ..." declarations → "Codeunit<id>" type names,
    // used to test extracted-DLL coverage without a full compile. Object-extension and
    // non-codeunit objects are intentionally ignored (only codeunits carry dispatchable
    // runtime bodies the test calls into).
    private static readonly System.Text.RegularExpressions.Regex _codeunitDecl =
        new(@"(?im)^\s*codeunit\s+(\d+)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static HashSet<string> ExtractCodeunitTypeNames(IReadOnlyList<(string Name, string Src)> sources)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, src) in sources)
            foreach (System.Text.RegularExpressions.Match mm in _codeunitDecl.Matches(src))
                set.Add("Codeunit" + mm.Groups[1].Value);
        return set;
    }

    private static string SanitizeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToArray();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    private static string SanitizeIdent(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Idempotent install of the default-ALC Resolving handler. Public so callers
    /// (e.g. Program.cs at startup) can install it before BcRuntime applies patches,
    /// in case a patch's reflection on a BC type triggers an assembly load for a
    /// transitively-referenced service-tier DLL that's not in the application bin.
    /// </summary>
    public static void EnsureResolverInstalled_Public() => EnsureResolverInstalled();

    private static void EnsureResolverInstalled()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        // BC service-tier artifact dir — same path BcRuntime/BcAssembler/Runner.csproj
        // resolve the 5 we project-reference (Types, Ncl, Common, Language, CodeAnalysis).
        // Microsoft.Dynamics.Nav.Ncl.dll transitively references ~24 BC DLLs, of which
        // we only project-reference 5; the rest sit in the artifact dir but aren't on
        // any probing path. When a generic instantiation or reflection call inside MS
        // R2R code reaches one (e.g. Microsoft.Dynamics.Nav.Core, .AL.Common, .Apps,
        // .TableProxyBuilder), it fails to load and the call NREs deep in MS code. The
        // probe below catches every Microsoft.Dynamics.Nav.* assembly request and serves
        // it from the artifact dir.
        // Single source of truth for the artifact dir (tracks AlRunner.csproj's _BCVersion).
        var serviceTierPath = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name == null) return null;
            if (_byName.TryGetValue(name.Name, out var asm))
                return asm;
            // Serve any service-tier assembly from the artifact dir. BC 28 modernised its
            // runtime onto a large external closure (Azure SDK, Microsoft.Identity / .Extensions,
            // IdentityModel) beyond the Microsoft.Dynamics.Nav.* set; all ship in the artifact
            // dir. This handler only fires after default resolution fails, so serving BC's own
            // shipped copy is the faithful choice.
            var probe = Path.Combine(serviceTierPath, name.Name + ".dll");
            if (File.Exists(probe))
                return ctx.LoadFromAssemblyPath(probe);
            return null;
        };
    }

    /// <summary>
    /// Lookup helper for callers that want to access a loaded dep by name
    /// (e.g. when verifying that a compile-time symbol matches a runtime one).
    /// </summary>
    public static Assembly? TryGetByAppId(Guid appId)
        => _cache.TryGetValue(appId, out var asm) ? asm : null;

    /// <summary>
    /// Record that <paramref name="asm"/> is the loaded module for AL app identity
    /// <paramref name="appId"/> — used by the bundle loop's own-AppGroup compile path
    /// (Program.cs) so an app compiled as ITS OWN bundle is visible here too, not just
    /// apps that arrived through <see cref="LoadAll"/>. Without this, a later bundle
    /// that resolves the same AppId as a *dependency* would MISS this cache and
    /// re-emit/re-compile a second, distinct module for the same AL identity — see
    /// issue #1683 (event-subscriber dispatch pairs a MethodInfo from one module's
    /// Type with a subscriberInstance from the other module's Type, throwing
    /// TargetException at ValidateInvokeTarget). First registration for a given AppId
    /// wins; a duplicate call for an AppId already present is a no-op (the earlier
    /// module is the one every other bundle should keep resolving to).
    /// </summary>
    public static void RegisterLoaded(Guid appId, Assembly asm)
        => _cache.TryAdd(appId, asm);
}
