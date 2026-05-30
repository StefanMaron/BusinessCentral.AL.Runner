// BcCompiler — in-process AL→C# compile via BC's own Compilation.Emit.
//
// Replaces the old AlEmitter (which shelled out to AlRunner --dump-csharp).
// The output bytes from this stage are ALREADY post-rewrite C# — BC's emitter
// applies the [NavByReferenceAttribute] T → ByRef<T> wrap natively at parameter
// declaration sites (see codeanalysis.cs:342854 EmitParameterType,
// codeanalysis.cs:342867 EmitMethodScopeFieldType, predicate at 340864
// ShouldBePassedByRef = IsVar && !IsArray && !IsUserType). v1's
// `--dump-csharp` is just `Console.WriteLine` of the same byte[] payload —
// the "before rewriting" label refers to v1's downstream RoslynRewriter, not
// to BC's compiler. So v2 no longer needs ByRefWrapRewriter.
//
// Wins over the subprocess path:
//   • ~88 % wall-time saving (no `dotnet AlRunner.dll` cold-start per bundle).
//   • No custom rewriter — BC's compiler already does the only mechanical
//     transformation that was happening in v2's syntax-rewrite pass.
//   • One in-memory Compilation per top-level arg, exactly mirroring v1's
//     `AlTranspiler.TranspileMulti` (AlRunner/Program.cs:1480) — single
//     compilation across all suite folders inside the bundle, just like the
//     existing AL emitter subprocess used to do.
//
// What still happens downstream (BcAssembler): parse the captured C# strings
// into Roslyn SyntaxTrees and CSharpCompilation.Emit() to produce IL. BC's
// service tier itself does the same two-stage AL→C#→IL handoff
// (Microsoft.Dynamics.Nav.Ncl.dll → NavAppPackageCompiler.RecompileFullPackage
//  → CSharpCompiler.Instance.CompileCSharpFilesAsync); the CSharpCompiler
// internal type is unreachable from out-of-process code (depends on
// NavEnvironment.Instance + live tenant context), so we own that step.
using System.Collections.Immutable;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using NavEmit = Microsoft.Dynamics.Nav.CodeAnalysis.Emit;
using NavDiag = Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using NavSymRef = Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;
using NavDotNet = Microsoft.Dynamics.Nav.CodeAnalysis.DotNet;

namespace AlRunnerV2;

public sealed record EmittedSource(string Name, string Code);

/// <summary>
/// Output of <see cref="BcCompiler.Emit"/>: emitted C# sources plus any AL-level
/// diagnostics (parse errors, declaration errors, emit-result errors) formatted
/// alc-style: <c>path(line,col): error ALXXXX: message</c>.
/// </summary>
public sealed record BcEmitOutput(IReadOnlyList<EmittedSource> Sources, IReadOnlyList<string> Diagnostics);

public sealed class BcCompiler
{
    /// <summary>
    /// Compile every .al file under <paramref name="alFolders"/> into a single
    /// in-memory Compilation; capture per-AL-object C# from the emit stage.
    /// </summary>
    /// <remarks>
    /// Mirrors v1's AlTranspiler.TranspileMulti shape (AlRunner/Program.cs:1480):
    /// one ParseOptions, one Compilation, parallel SyntaxTree.ParseObjectText.
    /// Exceptions during emit (the BC compiler throws AggregateException for
    /// individual method-body emit failures) are caught so partial output is
    /// still returned — same policy as v1 (Program.cs:1996).
    /// </remarks>
    // Lifted to static so the IReferenceLoader + SymbolReferenceSpecification[] are
    // built once per process. v1's pattern was "compile against a symbol reference
    // one app at a time"; per-suite emit + a shared loader is the in-process
    // equivalent. Bundling all suites into one Compilation ran into cross-suite
    // object-id collisions and silently produced 0 sources.
    private static NavCA.ISymbolReferenceLoader? _refLoader;
    // Content signature of the inputs _refLoader was built from (package dirs + extra
    // symbol dirs + resolved dep set). GetSharedReferences rebuilds the loader only when
    // this changes, so an unchanged dependency set keeps the warmed loader.
    private static string? _loaderSignature;
    private static NavCA.SymbolReferenceSpecification[]? _refSpecs;
    // Cached JSON symbol loaders — one per package dir that has *.symbols.json files.
    // Kept separately so specs can be recomputed with _currentAppId exclusion without
    // rescanning the filesystem.
    private static List<JsonSymbolReferenceLoader>? _cachedJsonLoaders;
    private static readonly object _refSync = new();
    // Set by Program.cs once after dep resolution. The compile-time symbol set
    // mirrors the runtime-loaded dep set by construction — no allow-list drift.
    private static IReadOnlyList<(AppManifest Manifest, string AppPath)>? _resolvedDeps;
    private static IReadOnlyList<string>? _packageCacheDirs;
    // Extra dirs that contain ONLY *.symbols.json files (no .app files). Used to
    // provide compile-time visibility of layered-build impls without exposing the
    // synthetic (SymbolReference.json-free) .app to the BC package scanner, which
    // would report AL1023 "package not valid". Set by RunLayeredPrePass.
    private static IReadOnlyList<string>? _extraSymbolDirs;

    // The bundle's real app.json identity, set per bundle before Emit. Used so the
    // main compilation matches internalsVisibleTo grants from its dependencies (BC
    // matches the grant by the consuming compilation's appId/publisher). Null → a
    // synthetic identity is used (the historical default).
    private static Guid? _currentAppId;
    private static string? _currentPublisher;
    private static Version? _currentVersion;

    /// <summary>Set the real app identity of the bundle about to be compiled, so
    /// internalsVisibleTo grants from its deps match. Pass nulls to reset.</summary>
    public static void SetCurrentAppIdentity(Guid? appId, string? publisher, Version? version)
    {
        lock (_refSync) { _currentAppId = appId; _currentPublisher = publisher; _currentVersion = version; }
    }

    /// <summary>
    /// Temporarily overrides the "current app being compiled" identity for the
    /// duration of a single sub-compile (e.g. DependencyLoader compiling a dep from
    /// source). The override is scoped: the caller MUST dispose the returned
    /// <see cref="IDisposable"/> (use a <c>using</c> block) to restore the previous
    /// identity. The <see cref="GetSharedReferences"/> self-reference guard uses
    /// <c>_currentAppId</c> to exclude the dep's own AppId from reference specs so
    /// its own AL source doesn't collide with a stale cached reference (AL0275).
    /// </summary>
    public static IDisposable ScopeCurrentAppIdentity(Guid appId, string publisher, Version version)
    {
        Guid? savedId;
        string? savedPublisher;
        Version? savedVersion;
        lock (_refSync)
        {
            savedId = _currentAppId;
            savedPublisher = _currentPublisher;
            savedVersion = _currentVersion;
            _currentAppId = appId;
            _currentPublisher = publisher;
            _currentVersion = version;
        }
        return new IdentityScope(savedId, savedPublisher, savedVersion);
    }

    private sealed class IdentityScope : IDisposable
    {
        private readonly Guid? _savedId;
        private readonly string? _savedPublisher;
        private readonly Version? _savedVersion;

        public IdentityScope(Guid? savedId, string? savedPublisher, Version? savedVersion)
        {
            _savedId = savedId;
            _savedPublisher = savedPublisher;
            _savedVersion = savedVersion;
        }

        public void Dispose()
        {
            lock (_refSync) { _currentAppId = _savedId; _currentPublisher = _savedPublisher; _currentVersion = _savedVersion; }
        }
    }

    // Cached DotNet resolver factory — constructed once from the service-tier
    // artifacts dir so AL `DotNet` variable types resolve to real .NET types.
    // Without this, NavTypeKind stays None and Compilation.Emit throws
    // UnexpectedValue(NavTypeKind.None) for any AL object with DotNet interop.
    private static NavDotNet.IDotNetResolverFactory? _dotNetResolverFactory;
    private static readonly object _dotNetSync = new();

    // When true (set by --precompile), the symbol-reference fallback enumerates
    // all discoverable .app files in the package cache dirs. This is needed for
    // apps whose NavxManifest.xml <Dependencies/> is empty but whose AL source
    // uses `using` statements that require BaseApp/System Application symbols.
    // Left false for corpus runs (where SetResolvedDeps provides the dep list).
    private static bool _usePackageCacheFallback;
    private static Guid _packageCacheFallbackExcludeId;

    /// <summary>
    /// Called from the --precompile path to enable the all-packages fallback for
    /// apps that declare no manifest deps. <paramref name="excludeAppId"/> is the
    /// AppId of the app being compiled — excluded to avoid AL0275 self-reference errors.
    /// </summary>
    public static void SetPackageCacheFallback(Guid excludeAppId)
    {
        lock (_refSync)
        {
            _usePackageCacheFallback = true;
            _packageCacheFallbackExcludeId = excludeAppId;
            _refLoader = null;
            _refSpecs = null;
            _cachedJsonLoaders = null;
        }
    }

    /// <summary>
    /// Resets the package-cache fallback to off and clears the cached loader/specs so
    /// the next call to <see cref="GetSharedReferences"/> rebuilds from the explicit
    /// dep list. Call after a scoped <see cref="SetPackageCacheFallback"/> use
    /// (e.g. inside <c>RunLayeredPrePass</c> per-impl symbol emit) to avoid leaking
    /// the all-packages scan into subsequent corpus or main-bundle compiles.
    /// </summary>
    public static void ResetPackageCacheFallback()
    {
        lock (_refSync)
        {
            _usePackageCacheFallback = false;
            _packageCacheFallbackExcludeId = default;
            _refLoader = null;
            _refSpecs = null;
            _cachedJsonLoaders = null;
        }
    }

    /// <summary>
    /// Registers extra symbol-only directories (containing <c>*.symbols.json</c> but no
    /// <c>.app</c> files) that <see cref="GetSharedReferences"/> should include in its
    /// <see cref="JsonSymbolReferenceLoader"/> chain. Call AFTER <see cref="SetResolvedDeps"/>
    /// so the cache invalidation there doesn't wipe this state. Resets when
    /// <see cref="SetResolvedDeps"/> is called again (next bundle).
    /// </summary>
    public static void SetExtraSymbolDirs(IReadOnlyList<string> dirs)
    {
        lock (_refSync)
        {
            _extraSymbolDirs = dirs;
            // The loader rebuild is driven by ComputeLoaderSignature (which includes the
            // extra dirs), so changing them triggers a rebuild on the next
            // GetSharedReferences — without unconditionally discarding the warm loader.
        }
    }

    // The service-tier artifacts dir mirrors BcAssembler.ServiceTierDir.
    // It contains the DLLs (XmlTextReader etc.) that BC DotNet interop resolves against.
    internal static readonly string DefaultServiceTierDir =
        AlRunnerV2.Infrastructure.BcArtifacts.ServiceTierDir;

    private static NavDotNet.IDotNetResolverFactory GetOrCreateDotNetFactory()
    {
        lock (_dotNetSync)
        {
            if (_dotNetResolverFactory != null)
                return _dotNetResolverFactory;

            // Probing paths: service-tier artifacts dir (BC's own .NET deps
            // such as Aspose, Azure SDK, BouncyCastle etc. shipped alongside Ncl.dll)
            // plus the runtime's own base-class library location.
            var probingPaths = new List<string>();
            if (Directory.Exists(DefaultServiceTierDir))
                probingPaths.Add(DefaultServiceTierDir);
            // BCL: where mscorlib / System.* lives (net10.0 shared framework).
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            if (Directory.Exists(runtimeDir))
                probingPaths.Add(runtimeDir);

            var locator = new NavDotNet.AssemblyLocator(probingPaths);
            _dotNetResolverFactory = new NavDotNet.DotNetResolverFactory(locator);
            return _dotNetResolverFactory;
        }
    }

    /// <summary>
    /// Set by Program.cs after DependencyResolver runs. The set of .app paths
    /// passed here is exactly what DependencyLoader will load at runtime, so
    /// compile-time symbols == runtime types by construction.
    /// </summary>
    public static void SetResolvedDeps(
        IReadOnlyList<(AppManifest Manifest, string AppPath)> deps,
        IReadOnlyList<string> packageCacheDirs)
    {
        lock (_refSync)
        {
            _resolvedDeps = deps;
            _packageCacheDirs = packageCacheDirs;
            _refSpecs = null;        // specs are cheap; recomputed per GetSharedReferences call
            _extraSymbolDirs = null; // reset so stale layered-build dirs don't leak to next bundle
            // NOTE: do NOT null _refLoader here. GetSharedReferences rebuilds the (expensive)
            // loader only when its content signature changes (ComputeLoaderSignature), so an
            // unchanged dep set keeps the warm loader instead of re-running the ~40s
            // WarmReferenceLoader on every call. This also lets --watch reuse warm deps.
        }
    }

    /// <summary>
    /// A stable content signature of the inputs the reference loader is built from, so the
    /// loader (and its ~40s warm) is rebuilt only when the dependency closure actually
    /// changes — not on every SetResolvedDeps/SetExtraSymbolDirs call or every bundle.
    /// </summary>
    private static string ComputeLoaderSignature(
        List<string> packageDirs,
        IReadOnlyList<string>? extraSymbolDirs,
        IReadOnlyList<(AppManifest Manifest, string AppPath)>? deps)
    {
        var parts = new List<string>();
        foreach (var d in packageDirs.OrderBy(x => x, StringComparer.Ordinal)) parts.Add("P:" + d);
        if (extraSymbolDirs != null)
            foreach (var d in extraSymbolDirs.OrderBy(x => x, StringComparer.Ordinal)) parts.Add("X:" + d);
        if (deps != null)
            foreach (var t in deps.OrderBy(x => x.AppPath, StringComparer.Ordinal))
                parts.Add("D:" + t.AppPath + "@" + t.Manifest.Version);
        return string.Join("\n", parts);
    }

    private static (NavCA.ISymbolReferenceLoader? Loader, NavCA.SymbolReferenceSpecification[] Specs)
        GetSharedReferences(IEnumerable<string> bundleAlpackagesDirs)
    {
        lock (_refSync)
        {
            // ── Loader (expensive filesystem scan + symbol warm) — cache and reuse ──
            // The loader scans package dirs for .app files and serves ModuleDefinitions,
            // then WarmReferenceLoader sequentially reads every reachable symbol spec
            // (~40s for a heavy Base App dep set). This is pure dependency work — it does
            // not depend on the bundle source — so it is rebuilt ONLY when its content
            // signature (package dirs + extra symbol dirs + resolved dep set) changes.
            // Unchanged deps → the warm loader is reused across calls, across bundles, and
            // across --watch re-runs.
            var packageDirs = bundleAlpackagesDirs
                .Where(Directory.Exists)
                .Distinct()
                .ToList();
            if (_packageCacheDirs != null)
                packageDirs.AddRange(_packageCacheDirs.Where(Directory.Exists));
            else
                packageDirs.AddRange(ResolveSymbolDirs());
            packageDirs = packageDirs.Distinct().ToList();

            var loaderSig = ComputeLoaderSignature(packageDirs, _extraSymbolDirs, _resolvedDeps);
            if (_refLoader == null || loaderSig != _loaderSignature)
            {
                if (packageDirs.Count == 0) return (null, Array.Empty<NavCA.SymbolReferenceSpecification>());

                _refLoader = NavSymRef.ReferenceLoaderFactory.CreateReferenceLoader(packageDirs);

                // Chain JSON-symbols loaders for any `*.symbols.json` in the package dirs
                // (written by EmitDepSymbols for source dependencies we compiled ourselves).
                // The standard scanner only reads a .app's SymbolReference.json, which a
                // synthetic source-dep .app lacks — so without this a source dep is
                // runtime-loadable but compile-invisible (AL0185). JSON loaders go FIRST
                // so they answer for those deps; they return null for everything else,
                // falling through to the package scanner.
                //
                // IMPORTANT: _extraSymbolDirs are scanned for *.symbols.json ONLY — they
                // must NOT be included in packageDirs above (passed to CreateReferenceLoader)
                // because they may contain synthetic .app files with no SymbolReference.json
                // (written by RunLayeredPrePass). If such an .app ends up in the .app scanner,
                // BC reports AL1023 "package not valid" for every compilation.
                var jsonScanDirs = packageDirs.ToList();
                if (_extraSymbolDirs != null)
                    foreach (var d in _extraSymbolDirs)
                        if (Directory.Exists(d) && !jsonScanDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                            jsonScanDirs.Add(d);

                _cachedJsonLoaders = jsonScanDirs
                    .Select(d => new JsonSymbolReferenceLoader(d))
                    .Where(l => l.HasAny)
                    .ToList();
                if (_cachedJsonLoaders.Count > 0)
                    _refLoader = new CompositeSymbolReferenceLoader(
                        _cachedJsonLoaders.Cast<NavCA.ISymbolReferenceLoader>().Append(_refLoader).ToList());

                // Pre-warm the loader's internal package caches SEQUENTIALLY before the
                // compiler's parallel reference-loading runs. BC's ReferenceManager fans
                // GetDependencies out across ThreadPool workers; concurrent first-reads of
                // the same R2R .app race inside NavAppPackageReader.CreateEmbeddedReader and
                // wedge in an unbounded Stream.CopyTo (intermittent compile hang on bundles
                // that pull MS test-library deps — proven gone when the process is pinned to
                // one CPU). Warming every reachable spec here makes that later parallel phase
                // hit warm caches instead of racing on cold file reads. Best-effort: any
                // failure just leaves the cold-read path to the compiler as before.
                WarmReferenceLoader(_refLoader, _resolvedDeps);
                _loaderSignature = loaderSig;
            }

            // ── Specs (cheap) — recompute each call with _currentAppId exclusion ──
            // Specs are just a list of (publisher, name, version, appId) tuples derived
            // from _resolvedDeps. Recomputing is trivial, and doing so ensures the
            // self-reference guard (_currentAppId) is applied fresh for EVERY compile:
            //   • main bundle compile: _currentAppId = bundle's own AppId → exclude self
            //   • dep compile inside DependencyLoader: _currentAppId = parent bundle's id,
            //     BUT the dep's AppId must be excluded too (it is its own primary source).
            //     DependencyLoader sets _currentAppId to the dep's AppId before calling
            //     BcCompiler.Emit, so the guard fires correctly for dep compiles as well.
            //   • EmitDepSymbols (pre-pass): _currentAppId = impl's AppId (set via
            //     SetCurrentAppIdentity in RunLayeredPrePass) → exclude self-spec.
            NavCA.SymbolReferenceSpecification[] specs;

            if (_resolvedDeps != null && _resolvedDeps.Count > 0)
            {
                // Normal path: explicit dep list from DependencyResolver.
                // Exclude the dep whose AppId == _currentAppId — that dep is the PRIMARY
                // source being compiled right now (either a main bundle being compiled as
                // itself, or a sub-dep being compiled inside DependencyLoader). Including it
                // as a reference alongside its own AL source causes AL0275 ambiguous-reference.
                specs = _resolvedDeps
                    .Where(d => _currentAppId == null || d.Manifest.AppId != _currentAppId.Value)
                    .Select(d => new NavCA.SymbolReferenceSpecification(
                        publisher: d.Manifest.Publisher,
                        name: d.Manifest.Name,
                        version: d.Manifest.Version,
                        exact: false,
                        appId: d.Manifest.AppId,
                        isPropagated: false,
                        alternateIds: ImmutableArray<Guid>.Empty))
                    .ToArray();
            }
            else if (_usePackageCacheFallback)
            {
                // --precompile path only: no explicit dep list (e.g. Customizations.app with
                // empty <Dependencies/>). Fall back to adding every discoverable .app in the
                // package cache dirs as a symbol reference — exactly what `alc --packagecachepath`
                // does implicitly. Covers apps that declare no manifest deps but still compile
                // against BaseApp/System Application via namespace-qualified `using` statements.
                // _packageCacheFallbackExcludeId: skip the app being compiled (avoids AL0275).
                var loaderPackageDirs = _packageCacheDirs?.Where(Directory.Exists).ToList()
                    ?? ResolveSymbolDirs().Where(Directory.Exists).ToList();
                var byId = new Dictionary<Guid, NavCA.SymbolReferenceSpecification>();
                foreach (var dir in loaderPackageDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var appFile in Directory.EnumerateFiles(dir, "*.app", SearchOption.AllDirectories))
                    {
                        var m = AppLoader.ReadManifest(appFile);
                        if (m == null || byId.ContainsKey(m.AppId)) continue;
                        if (_packageCacheFallbackExcludeId != default
                            && m.AppId == _packageCacheFallbackExcludeId) continue;
                        byId[m.AppId] = new NavCA.SymbolReferenceSpecification(
                            publisher: m.Publisher,
                            name: m.Name,
                            version: m.Version,
                            exact: false,
                            appId: m.AppId,
                            isPropagated: false,
                            alternateIds: ImmutableArray<Guid>.Empty);
                    }
                }
                specs = byId.Values.ToArray();
            }
            else
            {
                specs = Array.Empty<NavCA.SymbolReferenceSpecification>();
            }

            // Contribute specs for *.symbols.json deps so the compiler's reference
            // resolver sees them (the .app scanner above emits specs only for .app
            // files). Dedupe by AppId against the specs already built — a source dep
            // resolved as a (symbol-less) .app is already specced, and the composite
            // loader will satisfy it from the JSON loader.
            // Self-reference guard: skip any spec whose AppId == _currentAppId so a
            // bundle that previously emitted its OWN symbols.json into a workspace dir
            // (via RunLayeredPrePass) doesn't see those symbols when it is later compiled
            // as its own bundle (avoids AL1023 "package not valid") or as a dep
            // (avoids AL0275 "ambiguous reference").
            if (_cachedJsonLoaders != null && _cachedJsonLoaders.Count > 0)
            {
                var have = new HashSet<Guid>(specs.Select(s => s.AppId));
                var extra = _cachedJsonLoaders
                    .SelectMany(jl => jl.EnumerateSpecs())
                    .Where(s => _currentAppId == null || s.AppId != _currentAppId.Value)
                    .Where(s => have.Add(s.AppId))
                    .Select(s => new NavCA.SymbolReferenceSpecification(
                        publisher: s.Publisher, name: s.Name, version: s.Version,
                        exact: false, appId: s.AppId, isPropagated: false,
                        alternateIds: ImmutableArray<Guid>.Empty))
                    .ToArray();
                if (extra.Length > 0)
                    specs = specs.Concat(extra).ToArray();
            }
            _refSpecs = specs; // keep for any legacy callers that read _refSpecs directly
            return (_refLoader, specs);
        }
    }

    /// <summary>
    /// Sequentially walk every reachable dependency spec through the loader once, so its
    /// internal package caches are warm before the compiler's parallel reference loading.
    /// Defeats the NavAppPackageReader.CreateEmbeddedReader CopyTo race on bundles that
    /// pull R2R MS test-library deps. Best-effort: swallows all failures (the compiler then
    /// just re-reads cold, exactly as before this warm existed).
    /// </summary>
    private static void WarmReferenceLoader(
        NavCA.ISymbolReferenceLoader loader,
        IReadOnlyList<(AppManifest Manifest, string AppPath)>? resolvedDeps)
    {
        if (loader == null || resolvedDeps == null || resolvedDeps.Count == 0) return;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<NavCA.SymbolReferenceSpecification>();
            foreach (var d in resolvedDeps)
                queue.Enqueue(new NavCA.SymbolReferenceSpecification(
                    publisher: d.Manifest.Publisher, name: d.Manifest.Name, version: d.Manifest.Version,
                    exact: false, appId: d.Manifest.AppId, isPropagated: false,
                    alternateIds: ImmutableArray<Guid>.Empty));

            while (queue.Count > 0)
            {
                var spec = queue.Dequeue();
                if (!seen.Add($"{spec.Publisher}|{spec.Name}|{spec.Version}")) continue;
                IEnumerable<NavCA.SymbolReferenceSpecification>? deps = null;
                try { deps = loader.GetDependencies(spec, new List<NavCA.Diagnostics.Diagnostic>()); }
                catch { /* best-effort warm */ }
                if (deps == null) continue;
                foreach (var dep in deps) queue.Enqueue(dep);
            }
        }
        catch { /* best-effort warm — never block compilation */ }
    }

    public BcEmitOutput Emit(IEnumerable<string> alFolders, string moduleName)
    {
        var dirs = alFolders.Where(Directory.Exists).Distinct().ToList();
        if (dirs.Count == 0)
            throw new InvalidOperationException("BcCompiler.Emit: no source folders");

        var alFiles = dirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.al", SearchOption.AllDirectories))
            .Distinct()
            .ToList();
        if (alFiles.Count == 0)
            throw new InvalidOperationException(
                $"BcCompiler.Emit: no .al files under {string.Join(", ", dirs)}");

        // Preprocessor symbols: CLEANSCHEMA1..25. v1 computes per-source max from
        // any #pragma the AL files set (Program.cs:1454-1462); we use the static
        // 1..25 set v2 was already shipping — sufficient for the tests/ corpus.
        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}"),
            documentationMode: NavCA.DocumentationMode.None);

        bool _timing = Environment.GetEnvironmentVariable("BCCOMPILER_TIMING") == "1";
        var _tw = System.Diagnostics.Stopwatch.StartNew();
        void _mark(string p) { if (_timing) Console.Error.WriteLine($"[emit-timing] {p}: {_tw.ElapsedMilliseconds}ms"); _tw.Restart(); }

        var trees = new NavSyntax.SyntaxTree[alFiles.Count];
        Parallel.For(0, alFiles.Count, i =>
        {
            var src = File.ReadAllText(alFiles[i]);
            trees[i] = NavSyntax.SyntaxTree.ParseObjectText(
                src, path: alFiles[i], encoding: null!, parseOpts, default);
        });
        _mark($"parse {alFiles.Count} files");

        // CompilationOptions: identical to v1 (Program.cs:1548-1555).
        var compOpts = new NavCA.CompilationOptions(
            continueBuildOnError: true,
            target: NavCA.CompilationTarget.OnPrem,
            generateOptions:
                NavCA.CompilationGenerationOptions.Code |
                NavCA.CompilationGenerationOptions.Navigation);

        // Identity: use the bundle's REAL app.json identity when set, else a synthetic
        // one. The real identity matters when a dependency grants this app access via
        // internalsVisibleTo — BC matches the grant against the consuming compilation's
        // appId/publisher, so a synthetic "AlRunnerV2"/deterministic-guid identity would
        // fail to match and produce AL0161 on the dep's Access=Internal members.
        var appId = _currentAppId ?? DeterministicGuid(moduleName);
        var compilation = NavCA.Compilation.Create(
            moduleName: moduleName,
            publisher: _currentPublisher ?? "AlRunnerV2",
            version: _currentVersion ?? new Version(1, 0, 0, 0),
            appId: appId,
            syntaxTrees: trees,
            options: compOpts);

        // Suite-local .alpackages (rare in v2's corpus today, but cheap to honour).
        var bundleAlpackages = dirs
            .SelectMany(d => Directory.EnumerateDirectories(d, ".alpackages", SearchOption.AllDirectories))
            .Distinct();
        var (refLoader, specs) = GetSharedReferences(bundleAlpackages);
        _mark($"GetSharedReferences ({specs.Length} specs)");
        if (refLoader != null)
        {
            compilation = compilation.WithReferenceLoader(refLoader);
            if (specs.Length > 0)
                compilation = compilation.AddReferences(specs);
        }

        // Attach a local DotNet resolver so AL `DotNet` variables resolve to real
        // .NET types. Without this the default NullDotNetResolverFactory leaves
        // NavTypeKind = None, causing Compilation.Emit to throw
        // UnexpectedValue(NavTypeKind.None) for every DotNet-using method.
        compilation = compilation.WithDotNetResolverFactory(GetOrCreateDotNetFactory());

        var outputter = new CaptureOutputter();
        Exception? caught = null;
        Microsoft.Dynamics.Nav.CodeAnalysis.Emit.EmitResult? emitResult = null;
        try
        {
            // Compilation.Emit returns an EmitResult with Success + Diagnostics. The
            // silent-zero failure mode (captured=0, no thrown exception) is when
            // EmitResult.Success=false because the internal Compile step caught
            // diagnostics rather than throwing. Capture the result so the diag
            // block can surface them — otherwise we have no signal at all.
            emitResult = compilation.Emit(NavCA.EmitOptions.Default, outputter);
        }
        catch (Exception ex) { caught = ex; }
        _mark("compilation.Emit (bind + IL gen)");

        if (Environment.GetEnvironmentVariable("BCCOMPILER_DIAG") == "1")
        {
            Console.Error.WriteLine($"[BcCompiler-diag] module={moduleName} alFiles={alFiles.Count} addCalls={outputter.AddCalls} captured={outputter.Captured.Count} lastAdded={outputter.LastAddedName ?? "<none>"} caught={caught?.GetType().Name ?? "<none>"} emitSuccess={emitResult?.Success}");
            if (emitResult != null && !emitResult.Success)
            {
                var emitErrs = emitResult.Diagnostics
                    .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error)
                    .ToList();
                Console.Error.WriteLine($"  EmitResult.Diagnostics: {emitErrs.Count} error(s)");
                foreach (var d in emitErrs.Take(20))
                    Console.Error.WriteLine($"    emit[{d.Id}] @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
                if (emitErrs.Count > 20)
                    Console.Error.WriteLine($"    ... and {emitErrs.Count - 20} more");
            }
            if (caught != null)
            {
                Console.Error.WriteLine($"  msg: {caught.Message.Split('\n', 2)[0]}");
                if (caught is AggregateException agg)
                {
                    var inners = agg.Flatten().InnerExceptions.ToList();
                    Console.Error.WriteLine($"  inner exceptions: {inners.Count}");
                    int verbose = Environment.GetEnvironmentVariable("BCCOMPILER_DIAG_VERBOSE") == "1" ? 50 : 5;
                    foreach (var inner in inners.Take(verbose))
                    {
                        // Group object+method extracted from the AggregateException.Message
                        // (each AL emit failure includes "Object:'X' Method:'Y'" in the
                        // AggregateException line for that inner — but the inner itself
                        // only carries the BC-internal NRE/InvalidOpEx). Print full inner
                        // message + stack to surface the actual BC emit code path.
                        Console.Error.WriteLine($"  inner[{inner.GetType().Name}]: {inner.Message}");
                        if (inner.StackTrace != null)
                        {
                            // Show the top BC-emitter frames so the failing CodeGenerator
                            // method is visible (Microsoft.Dynamics.Nav.CodeAnalysis.* path).
                            var topFrames = inner.StackTrace
                                .Split('\n')
                                .Where(l => l.Contains("Microsoft.Dynamics.Nav.CodeAnalysis"))
                                .Take(8);
                            foreach (var frame in topFrames)
                                Console.Error.WriteLine($"    {frame.Trim()}");
                        }
                        if (inner.InnerException != null)
                            Console.Error.WriteLine($"    causedby[{inner.InnerException.GetType().Name}]: {inner.InnerException.Message.Split('\n', 2)[0]}");
                    }
                    // The outer AggregateException.Message has "Object:'X' Method:'Y'"
                    // for each inner. Extract and print as a clean per-method list.
                    Console.Error.WriteLine("  failing methods (extracted from AggregateException msg):");
                    var rx = new System.Text.RegularExpressions.Regex(
                        @"Object:'([^']+)' Method:'([^']+)' \(([^)]+)\)");
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(caught.Message))
                        Console.Error.WriteLine($"    {m.Groups[1].Value} :: {m.Groups[2].Value}  [{m.Groups[3].Value}]");
                }
                else if (Environment.GetEnvironmentVariable("BCCOMPILER_DIAG_VERBOSE") == "1")
                {
                    Console.Error.WriteLine($"  full: {caught}");
                }
            }
            var declErrs = compilation.GetDeclarationDiagnostics()
                .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error).ToList();
            var parseErrs = trees.SelectMany(t => t.GetDiagnostics())
                .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error).ToList();
            Console.Error.WriteLine($"  declErrors={declErrs.Count} parseErrors={parseErrs.Count}");
            foreach (var d in parseErrs.Take(5))
                Console.Error.WriteLine($"    parse[{d.Id}] @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
            // AL0275 = ambiguous reference (the cross-suite conflict signal we care about).
            foreach (var d in declErrs.Where(d => d.Id == "AL0275"))
                Console.Error.WriteLine($"    AL0275 @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
            foreach (var d in declErrs.Where(d => d.Id != "AL0275").Take(10))
                Console.Error.WriteLine($"    {d.Id} @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
        }

        // Collect AL-level diagnostics for Program.cs to surface at the compile
        // boundary — formatted alc-style so they read like `alc.exe` output.
        var alDiags = new List<string>();
        var allParseErrs = trees
            .SelectMany(t => t.GetDiagnostics())
            .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error)
            .ToList();
        var allDeclErrs = compilation.GetDeclarationDiagnostics()
            .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error)
            .ToList();
        foreach (var d in allParseErrs)
            alDiags.Add($"{d.Location}: error {d.Id}: {d.GetMessage().Split('\n', 2)[0]}");
        foreach (var d in allDeclErrs)
            alDiags.Add($"{d.Location}: error {d.Id}: {d.GetMessage().Split('\n', 2)[0]}");
        if (emitResult != null && !emitResult.Success)
        {
            foreach (var d in emitResult.Diagnostics
                .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error))
                alDiags.Add($"{d.Location}: error {d.Id}: {d.GetMessage().Split('\n', 2)[0]}");
        }
        // When Compilation.Emit throws (BC's emitter crashed on a per-object bound
        // tree — e.g. an unresolved type reaching codegen), the AggregateException
        // message carries one "Object:'X' Method:'Y' (reason)" entry per failing
        // object. Surface them in the returned diagnostics so callers fail LOUDLY
        // with the failing object names by default — not only under BCCOMPILER_DIAG.
        // See loud-failures.md / runner issue #1620.
        if (caught != null)
        {
            var rx = new System.Text.RegularExpressions.Regex(
                @"Object:'([^']+)' Method:'([^']+)' \(([^)]+)\)");
            var matches = rx.Matches(caught.Message);
            foreach (System.Text.RegularExpressions.Match m in matches)
                alDiags.Add($"emit-crash: {m.Groups[1].Value} :: {m.Groups[2].Value} — {m.Groups[3].Value}");
            if (matches.Count == 0)
                // No per-object breakdown in the message — surface the raw emit failure.
                alDiags.Add($"emit-crash: {caught.GetType().Name}: {caught.Message.Split('\n', 2)[0]}");
        }

        return new BcEmitOutput(outputter.Captured, alDiags);
    }

    /// <summary>
    /// Compile a source-dependency app's AL into a BC Compilation and serialize its
    /// AL symbol metadata to <paramref name="symbolsJsonPath"/> (a `*.symbols.json`
    /// readable by <see cref="JsonSymbolReferenceLoader"/>). This is the
    /// compile-visible half of a source dependency — the runtime half is the DLL the
    /// DependencyLoader produces from the same source. The serialized symbols carry
    /// the dep's Access/internalsVisibleTo metadata, so a dependent app compiles
    /// against it with the boundary enforced (revived from main's DepCompiler; v2
    /// only shipped a symbol-less synthetic .app before, hence AL0185). The
    /// Compilation is created with the dep's REAL identity so the loader indexes it.
    /// </summary>
    public void EmitDepSymbols(
        IEnumerable<string> alFolders, string moduleName,
        Guid appId, string publisher, Version version, string symbolsJsonPath)
    {
        var dirs = alFolders.Where(Directory.Exists).Distinct().ToList();
        var alFiles = dirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.al", SearchOption.AllDirectories))
            .Distinct().ToList();
        if (alFiles.Count == 0)
            throw new InvalidOperationException(
                $"BcCompiler.EmitDepSymbols: no .al files under {string.Join(", ", dirs)}");

        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}"),
            documentationMode: NavCA.DocumentationMode.None);
        var trees = new NavSyntax.SyntaxTree[alFiles.Count];
        Parallel.For(0, alFiles.Count, i =>
        {
            var src = File.ReadAllText(alFiles[i]);
            trees[i] = NavSyntax.SyntaxTree.ParseObjectText(src, path: alFiles[i], encoding: null!, parseOpts, default);
        });
        var compOpts = new NavCA.CompilationOptions(
            continueBuildOnError: true,
            target: NavCA.CompilationTarget.OnPrem,
            generateOptions:
                NavCA.CompilationGenerationOptions.Code | NavCA.CompilationGenerationOptions.Navigation);
        // Propagate the dep's own `internalsVisibleTo` (from its app.json) into the
        // Compilation. BC populates IModuleSymbol.InternalsVisibleToModules ONLY from
        // this dedicated Create parameter — not from the manifest — so without it a
        // dependent app hits AL0161 on the dep's Access=Internal members even when the
        // grant exists. (main:Program.cs BuildInternalsVisibleToRefs.)
        var ivtRefs = ReadInternalsVisibleToRefs(
            dirs.Select(d => Path.Combine(d, "app.json")).FirstOrDefault(File.Exists));

        var compilation = NavCA.Compilation.Create(
            moduleName: moduleName, publisher: publisher, version: version,
            appId: appId, internalsVisibleTo: ivtRefs, syntaxTrees: trees, options: compOpts);

        var bundleAlpackages = dirs
            .SelectMany(d => Directory.EnumerateDirectories(d, ".alpackages", SearchOption.AllDirectories))
            .Distinct();
        var (refLoader, specs) = GetSharedReferences(bundleAlpackages);
        if (refLoader != null)
        {
            compilation = compilation.WithReferenceLoader(refLoader);
            if (specs.Length > 0) compilation = compilation.AddReferences(specs);
        }
        compilation = compilation.WithDotNetResolverFactory(GetOrCreateDotNetFactory());

        // Loud failure (per .claude/rules/loud-failures.md): if the dep does not compile,
        // surface the AL diagnostics here rather than letting WriteSymbolJson fail with a
        // cryptic "Unable to build ModuleDefinition" (the converter NREs on dangling symbols).
        var errors = compilation.GetDeclarationDiagnostics()
            .Where(d => d.Severity == NavCA.Diagnostics.DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"source dependency '{moduleName}' does not compile ({errors.Count} error(s)): " +
                string.Join("; ", errors.Take(10).Select(d => $"{d.Id} {d.GetMessage()}")));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(symbolsJsonPath))!);
        using var fs = new FileStream(symbolsJsonPath, FileMode.Create, FileAccess.Write, FileShare.None);
        SymbolJsonWriter.WriteSymbolJson(compilation, fs);
    }

    /// <summary>
    /// Read <c>internalsVisibleTo</c> from an app.json and return one
    /// <see cref="NavCA.SymbolReferenceSpecification"/> per entry, for the dedicated
    /// <c>internalsVisibleTo</c> parameter of <see cref="NavCA.Compilation.Create"/>.
    /// Schema: <c>[{ id|appId: guid, name, publisher }]</c>. Null when absent.
    /// </summary>
    private static IEnumerable<NavCA.SymbolReferenceSpecification>? ReadInternalsVisibleToRefs(string? appJsonPath)
    {
        if (appJsonPath == null || !File.Exists(appJsonPath)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
            if (!json.RootElement.TryGetProperty("internalsVisibleTo", out var ivt)
                || ivt.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var refs = new List<NavCA.SymbolReferenceSpecification>();
            foreach (var e in ivt.EnumerateArray())
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = e.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                var pub = e.TryGetProperty("publisher", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() ?? "" : "";
                Guid? appId = null;
                if ((e.TryGetProperty("id", out var idEl) || e.TryGetProperty("appId", out idEl))
                    && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(idEl.GetString(), out var gid))
                    appId = gid;
                // IVT matching is by publisher/name/appId; version is not part of the
                // schema, so a 0.0.0.0 placeholder is fine (BC does not gate IVT on version).
                refs.Add(new NavCA.SymbolReferenceSpecification(
                    publisher: pub, name: name!, version: new Version(0, 0, 0, 0),
                    exact: false, appId: appId));
            }
            return refs.Count > 0 ? refs : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolve symbol-package search dirs. Scans (in order):
    ///   1. `~/.local/share/al-runner/symbols/<bc-ver>/` — the v2-curated set
    ///      (Application + Base + System Application).
    ///   2. `~/.bcartifacts.cache/sandbox/<bc-ver>/w1/Extensions/` — full set
    ///      from the BC W1 artifact (Business Foundation, Library Assert,
    ///      Test Runner, Library Variable Storage, etc.).
    ///   3. `~/.bcartifacts.cache/sandbox/<bc-ver>/platform/Applications/` —
    ///      platform Test Library apps.
    /// Picks the highest BC version found in each pool.
    /// </summary>
    private static IEnumerable<string> ResolveSymbolDirs()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) yield break;

        foreach (var rel in new[] { ".local/share/al-runner/symbols", ".bcartifacts.cache/sandbox" })
        {
            var root = Path.Combine(home, rel);
            if (!Directory.Exists(root)) continue;
            var bestVer = Directory.EnumerateDirectories(root)
                .Select(d => (Dir: d, Ver: System.Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(t => t.Ver != null)
                .OrderByDescending(t => t.Ver)
                .Select(t => t.Dir)
                .FirstOrDefault();
            if (bestVer == null) continue;

            if (rel.StartsWith(".local"))
            {
                yield return bestVer;
            }
            else
            {
                // bcartifacts.cache/sandbox/<ver>/{w1/Extensions, platform/Applications}
                var w1Ext = Path.Combine(bestVer, "w1", "Extensions");
                if (Directory.Exists(w1Ext)) yield return w1Ext;
                var platApps = Path.Combine(bestVer, "platform", "Applications");
                if (Directory.Exists(platApps)) yield return platApps;
            }
        }
    }

    private static Guid DeterministicGuid(string seed)
    {
        // Hash the seed and reuse the first 16 bytes as a GUID. Stable, no crypto.
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// CodeModuleOutputter override that accumulates UTF-8 C# bytes per AL object.
    /// Mirrors v1's CSharpCaptureOutputter (AlRunner/Program.cs:4516).
    /// </summary>
    private sealed class CaptureOutputter : NavEmit.CodeModuleOutputter
    {
        public List<EmittedSource> Captured { get; } = new();
        public string? LastAddedName { get; private set; }
        public int AddCalls { get; private set; }

        public CaptureOutputter() : base(NavCA.EmitOptions.Default) { }

        public override void InitializeModule(NavCA.IModuleSymbol moduleSymbol) { }

        public override void AddApplicationObject(
            NavCA.IApplicationObjectTypeSymbol symbol,
            byte[] code, string metadata, string debugCode)
        {
            AddCalls++;
            LastAddedName = symbol.Name;
            var src = System.Text.Encoding.UTF8.GetString(code);
            Captured.Add(new EmittedSource(symbol.Name, src));

            // Capture (id, name, options[], indexes[]) for AL enum types so the
            // runtime NCLEnumMetadata.Create(int) hook can return real
            // GetNames()/GetOrdinals() data instead of NCLOptionMetadata.Default
            // (which throws NavNCLNotSupportedOperationException). Enum
            // extensions also flow through here as IEnumExtensionTypeSymbol;
            // both expose Values via the IEnumBaseTypeSymbol interface.
            if (symbol is NavCA.IEnumBaseTypeSymbol enumSym)
            {
                var values = enumSym.Values;
                var options = new string[values.Length];
                var indexes = new int[values.Length];
                var implementations = new int[values.Length][];
                for (int i = 0; i < values.Length; i++)
                {
                    options[i] = values[i].Name ?? string.Empty;
                    indexes[i] = values[i].Ordinal;
                    implementations[i] = ReadEnumValueImplementations(values[i]);
                }
                AlEnumMetadataRegistry.Register(enumSym.Id, enumSym.Name, options, indexes, implementations);
            }
            if (Environment.GetEnvironmentVariable("BCCOMPILER_TRACE") == "1")
                Console.Error.WriteLine($"  emit[{AddCalls}]: {symbol.Name}");
            if (Environment.GetEnvironmentVariable("BCCOMPILER_DUMP_CS") == "1")
            {
                var dir = Path.Combine(Path.GetTempPath(), "bccompiler-dump");
                Directory.CreateDirectory(dir);
                var fname = string.Concat(symbol.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".cs";
                File.WriteAllText(Path.Combine(dir, fname), src);
            }
        }

        /// <summary>
        /// Read the resolved implementation-codeunit ids for one AL enum value's
        /// interface implementations, ordered by interface-declaration index.
        ///
        /// The compiler resolves the value's <c>Implementation</c> property to a
        /// comma-separated list of codeunit ids (e.g. <c>"60201"</c>, or
        /// <c>"60201,60202"</c> for an enum implementing two interfaces) — the
        /// same shape the prebuilt SymbolReference JSON carries, which
        /// <see cref="AlRunnerV2.Patches.BcAppSymbolCache"/> already parses. Capturing it
        /// here lets enum→interface casts (<c>ALCompiler.ToInterface(NavOption,index)</c>)
        /// resolve the implementing codeunit for enums compiled from source, not
        /// just for prebuilt MS/ISV apps. Without this the runner returned -1 and
        /// threw "Unable to cast enum '…' to interface at index N".
        /// </summary>
        private static int[] ReadEnumValueImplementations(NavCA.IEnumValueSymbol value)
        {
            try
            {
                var impl = value.GetProperty(NavCA.PropertyKind.Implementation);
                var text = impl?.ValueText;
                if (string.IsNullOrEmpty(text))
                    return Array.Empty<int>();
                var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var ids = new List<int>(parts.Length);
                foreach (var part in parts)
                    if (int.TryParse(part, out var id))
                        ids.Add(id);
                return ids.ToArray();
            }
            catch
            {
                return Array.Empty<int>();
            }
        }

        public override void AddProfileObject(
            NavCA.ISymbol symbol, byte[] code, string metadata, string debugCode) { }
        public override void AddNavigationObject(string content) { }
        public override void AddExternalBusinessEvent(string content) { }
        public override void AddMovedObjects(string content) { }
        public override void FinalizeModule() { }
        public override ImmutableArray<NavDiag.Diagnostic> GetDiagnostics()
            => ImmutableArray<NavDiag.Diagnostic>.Empty;
    }
}
