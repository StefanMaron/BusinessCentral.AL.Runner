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

namespace AlRunner;

public sealed record EmittedSource(string Name, string Code);

/// <summary>
/// Output of <see cref="BcCompiler.Emit"/>: emitted C# sources plus any AL-level
/// diagnostics (parse errors, declaration errors, emit-result errors) formatted
/// alc-style: <c>path(line,col): error ALXXXX: message</c>.
/// </summary>
/// <param name="ExcludedObjects">
/// Objects the emit-retry loop dropped to get the rest of the module to compile. NON-EMPTY
/// MEANS TESTS VANISHED: an excluded test codeunit contributes no results, so the run reports
/// a smaller total and still exits 0. Measured on the al-language corpus — a stale System.app
/// silently cost 7 tests (1904 -> 1897) with no output at any verbosity below --verbose.
/// The caller MUST treat this as a hard failure (.claude/rules/loud-failures.md).
/// </param>
/// <summary>
/// Per-excluded-object detail captured DURING the emit-retry loop (issue #1997 —
/// <c>--tdd</c>), before the retry loop's compilation/emitResult variables get
/// reassigned to the next round's smaller retry compile and the diagnostics that
/// identified this object become unreachable. Only populated when
/// <see cref="BcCompiler.SetTddMode"/> is on — see that method's doc comment for why.
/// </summary>
/// <param name="FilePath">
/// The excluded object's own source file — NOT a temp copy (precompiled-dll-respect.md
/// / this issue's "do not compile from a temp-directory copy" note applies equally to
/// this path: it is what makes a --tdd synthetic failure's message point at a real,
/// clickable file).
/// </param>
/// <param name="ObjectDisplayName">Matches the corresponding entry in <c>ExcludedObjects</c>.</param>
/// <param name="Diagnostics">
/// The AL diagnostics (alc-style: <c>path(line,col): error ALXXXX: message</c>) that
/// caused THIS object specifically to be excluded — not the whole module's diagnostics.
/// </param>
public sealed record TddExcludedObjectDetail(
    string FilePath,
    string ObjectDisplayName,
    IReadOnlyList<string> Diagnostics);

public sealed record BcEmitOutput(
    IReadOnlyList<EmittedSource> Sources,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> ExcludedObjects,
    IReadOnlyList<TddExcludedObjectDetail>? TddExcludedDetails = null,
    // --tdd (issue #2001): every member TddGeneration.Generate inferred and generated during
    // THIS Emit call, regardless of whether the object it targeted ultimately still ended up
    // excluded (a wrong guess still shows up here — see TddGeneration.cs's header for why
    // that's fine: the object's own exclusion is what catches a bad guess, not this list).
    // Null (not merely empty) when not in --tdd mode — same discipline as TddExcludedDetails.
    IReadOnlyList<TddGeneratedMember>? TddGeneratedMembers = null);

public sealed partial class BcCompiler
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
    // The .app package-scanner half of _refLoader, kept separately so a per-compile
    // self-exclusion (SelfExcludingSymbolReferenceLoader) can be layered on exactly it —
    // the JSON-symbols loaders chained ahead of it are unaffected by .app-level exclusion.
    // Sharing this one object across every compile is what makes the (per-instance,
    // 8–10 s) symbol warm a once-per-process cost instead of once-per-dependency (#1831).
    private static NavCA.ISymbolReferenceLoader? _refPackageLoader;
    // Content signature of the DIRECTORY inputs _refLoader was built from: the package dirs
    // it scans plus the extra symbol dirs. Paired with _loaderDepUniverse below — together
    // they decide when the loader is rebuilt.
    private static string? _loaderSignature;
    // The resolved-dep keys (`AppPath@Version`) the current _refLoader was BUILT FOR.
    //
    // #1832: the dep list used to be folded into _loaderSignature, i.e. compared for
    // EQUALITY, so any change to it — including one that only REMOVED entries — rebuilt the
    // loader. `ScopeSymbolBearingDepsOnly` removes entries (the synthetic source-only .apps)
    // around every compile that inspects declaration diagnostics, so entering it cost a full
    // rebuild + per-instance symbol warm: 14.3 s of the 35.8 s `sibling-symbols` stage on a
    // cold tests/runner-extras bundle.
    //
    // The comparison is now SUBSET, not equality: the loader is reused when the current dep
    // list is a subset of the one it was built for. Removal is provably free — a BC
    // reference loader is constructed from a directory scan (CreateReferenceLoader never
    // sees the dep list), the removed dep's files are still on disk in the same dirs, and
    // the dep list's two real jobs are both redone per call anyway: the requested SPEC array
    // is rebuilt at the bottom of GetSharedReferences (so it really does shrink), and
    // WarmReferenceLoader is a read-only cache prefill (so a superset warm subsumes a subset
    // one). Anything that ADDS or CHANGES a key still rebuilds, exactly as before — which
    // matters: a source dependency recompiled from edited AL is republished under a NEW
    // content-addressed path (`~/.cache/al-runner/workspace-deps/<hash>/…`), and that new
    // key is what makes the next compile see the new symbols instead of the cached loader's
    // stale ones (scripts/tests/server-mode-test.sh assertions 2+3).
    private static HashSet<string>? _loaderDepUniverse;
    // How many times the expensive loader (filesystem scan + CreateReferenceLoader +
    // WarmReferenceLoader) has actually been built in this process, counting both the
    // shared superset loader and any physically-excluded fallback. The whole point of
    // #1831 is that this stays at 1 across a bundle's dependency compiles; asserting on
    // the COUNT is what BcCompilerSharedReferenceMemoTests pins (a duration assertion
    // would be flaky and would not distinguish "fast" from "correct").
    private static int _loaderBuildCount;
    internal static int ReferenceLoaderBuildCount { get { lock (_refSync) return _loaderBuildCount; } }
    // How many distinct symbol specs WarmReferenceLoader has actually pushed through a
    // loader in this process. The companion to _loaderBuildCount for #1832: reusing the
    // loader object is only a win if the warm is not redone on top of it, and "warm work
    // performed" is a count a test can assert exactly, unlike a duration.
    private static int _warmSpecCount;
    internal static int ReferenceLoaderWarmSpecCount { get { lock (_refSync) return _warmSpecCount; } }
    // Single-slot memo for the rare physically-reduced loader — see the fallback branch in
    // GetSharedReferences. Keyed exactly as the pre-#1831 memo was (reduced scan dirs +
    // excluded AppId), so a run that needs it behaves exactly as it did before.
    private static NavCA.ISymbolReferenceLoader? _exclPackageLoader;
    private static string? _exclSignature;
    // _loaderDepUniverse's counterpart for the fallback loader instance.
    private static HashSet<string>? _exclDepUniverse;
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
    // Symbols for apps emitted EARLIER IN THIS SAME BUNDLE, so a later app can reference a
    // sibling it depends on. Deliberately NOT part of the loader signature: it is chained on
    // top of the cached loader per call and re-indexed in place, so adding a sibling never
    // triggers a rebuild of the expensive scan+warm. See SetSiblingSymbolsDir.
    private static JsonSymbolReferenceLoader? _siblingSymbols;

    /// <summary>
    /// Point the compiler at a directory that will receive <c>*.symbols.json</c> for apps
    /// emitted earlier in the current bundle, and reset any previous bundle's. Pass null to
    /// clear.
    ///
    /// Bundled mode emits ONE MODULE PER app.json, ordered so an app follows every sibling it
    /// depends on — but nothing made the emitted sibling VISIBLE to the next compile, so
    /// `*-main` could not see `*-dep` (AL0185 "Codeunit 'XMI Dep Api' is missing") and its
    /// whole test codeunit was dropped by the emit-retry.
    /// </summary>
    public static void SetSiblingSymbolsDir(string? dir)
    {
        lock (_refSync)
        {
            _siblingSymbols = dir == null ? null : new JsonSymbolReferenceLoader(dir);
        }
    }

    /// <summary>
    /// Re-index the sibling-symbols directory after another app's symbols were written into
    /// it. Cheap: no loader rebuild, no symbol warm.
    /// </summary>
    public static void RefreshSiblingSymbols()
    {
        lock (_refSync) { _siblingSymbols?.Reindex(); }
    }

    /// <summary>
    /// Temporarily drop resolved deps whose .app carries no <c>SymbolReference.json</c> from
    /// the compile spec list, restoring the full set on dispose.
    ///
    /// Such a package is a SYNTHETIC source-only .app (InProcessAppPackager): it exists so
    /// DependencyResolver can find the dep and the loader can compile it from source, and it
    /// can never serve the compiler's native .app scanner — requesting a spec for it yields
    /// AL1023 ("package file is not valid") and then AL1022 ("could not be found"). The
    /// primary Compilation.Emit path never inspects declaration diagnostics so it shrugs
    /// these off, but any compile that DOES check them (EmitDepSymbols) fails outright on a
    /// package that has nothing to do with the AL it is compiling. Their symbols reach the
    /// compiler the intended way regardless — through *.symbols.json and the JSON loader,
    /// whose specs are contributed separately in GetSharedReferences.
    ///
    /// The "does this .app carry a SymbolReference.json" question goes through
    /// <see cref="ReadAppMeta"/>, the same per-file (path + length + last-write-ticks) cache
    /// DeduplicateAppPackageDirs uses. Calling <see cref="AppLoader.HasSymbolReference"/>
    /// directly re-read and unzipped every resolved dep's WHOLE package on each scope entry,
    /// and a bundled run enters this scope once per app group: measured 20.1 s across the 38
    /// scope entries of a cold tests/runner-extras bundle (#1832). The cache invalidates on
    /// an in-place rewrite, so a synthetic .app re-packaged mid-run is still re-read.
    /// </summary>
    public static IDisposable ScopeSymbolBearingDepsOnly()
    {
        IReadOnlyList<(AppManifest Manifest, string AppPath)>? saved;
        lock (_refSync)
        {
            saved = _resolvedDeps;
            if (saved != null)
            {
                var filtered = saved.Where(d => ReadAppMeta(new FileInfo(d.AppPath)).HasSymbolReference).ToList();
                if (filtered.Count != saved.Count)
                {
                    _resolvedDeps = filtered;
                    _refSpecs = null;
                }
                else saved = null; // nothing removed — restore is a no-op
            }
        }
        return new RestoreResolvedDeps(saved);
    }

    private sealed class RestoreResolvedDeps : IDisposable
    {
        private readonly IReadOnlyList<(AppManifest Manifest, string AppPath)>? _saved;
        public RestoreResolvedDeps(IReadOnlyList<(AppManifest, string)>? saved) => _saved = saved;
        public void Dispose()
        {
            if (_saved == null) return;
            lock (_refSync) { _resolvedDeps = _saved; _refSpecs = null; }
        }
    }
    // --tdd (issue #1997): off by default. When on, the emit-retry loop in Emit()
    // additionally captures per-excluded-object TddExcludedObjectDetail (file path +
    // the diagnostics that identified it) into the returned BcEmitOutput, so the
    // caller (Program.cs) can turn each excluded object's [Test] procedures into
    // synthetic FAILED TestResults instead of discarding the whole module — see
    // Program.cs's EMIT-EXCLUDED handling. Gated behind this flag (rather than always
    // capturing) so the default (non-tdd) path is PROVABLY unchanged: no extra
    // allocation, no extra diagnostic formatting work, on every ordinary run.
    private static bool _tddMode;

    /// <summary>
    /// Sets whether the emit-retry loop should capture <see cref="TddExcludedObjectDetail"/>
    /// for excluded objects. Follows the same static-setter pattern as
    /// <see cref="SetExtraPreprocessorSymbols"/> — compile-affecting CLI options are pushed
    /// into BcCompiler this way because there are four Emit call sites in Program.cs and no
    /// single place to thread a parameter through all of them.
    /// </summary>
    public static void SetTddMode(bool enabled)
    {
        lock (_refSync) { _tddMode = enabled; }
    }

    /// <summary>True when a --tdd run is in progress. Read-only mirror of <see cref="SetTddMode"/>.</summary>
    public static bool IsTddMode()
    {
        lock (_refSync) { return _tddMode; }
    }

    // Extra preprocessor symbols supplied by the caller via --define / --preprocessor-symbols.
    // Merged with the built-in CLEANSCHEMA1..25 set at both ParseOptions sites.
    private static IReadOnlyList<string>? _extraPreprocessorSymbols;

    /// <summary>
    /// Registers additional preprocessor symbols (e.g. from --define MY_SYM) that are
    /// merged with the built-in CLEANSCHEMA1..25 set at every <see cref="NavCA.ParseOptions"/>
    /// call. Symbols must be valid AL identifiers (validated by the caller).
    /// </summary>
    public static void SetExtraPreprocessorSymbols(IReadOnlyList<string> symbols)
    {
        lock (_refSync)
        {
            _extraPreprocessorSymbols = symbols;
        }
    }

    /// <summary>
    /// The extra preprocessor symbols registered via <see cref="SetExtraPreprocessorSymbols"/>,
    /// sorted for a stable order. These change which <c>#if</c> branch compiles, so the AL
    /// cache key must include them — otherwise a bare run and a <c>--define</c> run over the
    /// same sources hash identically and the second one silently reuses the first one's DLL.
    /// </summary>
    public static IReadOnlyList<string> GetExtraPreprocessorSymbols()
    {
        lock (_refSync)
        {
            return _extraPreprocessorSymbols is null
                ? []
                : _extraPreprocessorSymbols.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Returns true if <paramref name="symbol"/> is a valid AL preprocessor identifier:
    /// one or more characters from [A-Za-z0-9_], not starting with a digit.
    /// </summary>
    public static bool IsValidPreprocessorSymbol(string symbol)
    {
        if (symbol.Length == 0) return false;
        if (char.IsDigit(symbol[0])) return false;
        foreach (var c in symbol)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

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
        AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;

    private static NavDotNet.IDotNetResolverFactory GetOrCreateDotNetFactory()
    {
        lock (_dotNetSync)
        {
            if (_dotNetResolverFactory != null)
                return _dotNetResolverFactory;

            // Probing paths, in priority order. BC's DotNet metadata reader resolves a
            // `DotNet "Type"` alias by loading the named assembly and looking up the type
            // as a *TypeDefinition* — it does NOT follow type-forwarders. The runtime
            // `netstandard.dll` / `System.Xml.*` shipped in the shared-framework dir are
            // pure forwarder *facades* (1 typeDef, ~2600 ExportedType forwarders), so a
            // `DotNet "System.Xml.XmlException"` alias against `netstandard, 2.1.0.0`
            // binds to NavTypeKind.None there → emit crashes with "Unexpected value 'None'".
            // The matching *reference* assemblies (NETStandard.Library.Ref, the NETCore.App
            // ref pack) carry the same types as real TypeDefinitions, so probe those FIRST.
            // This is what lets the source-dependency compile of Microsoft's Tests-TestLibraries
            // (XmlDocument/XmlNode/etc. interop) emit instead of zeroing the whole module.
            var probingPaths = new List<string>();
            foreach (var refDir in EnumerateDotNetRefAssemblyDirs())
                if (Directory.Exists(refDir))
                    probingPaths.Add(refDir);
            // BC service-tier artifacts dir (BC's own .NET deps such as Aspose, Azure SDK,
            // BouncyCastle etc. shipped alongside Ncl.dll, plus PermissionTestHelper add-in).
            if (Directory.Exists(DefaultServiceTierDir))
                probingPaths.Add(DefaultServiceTierDir);
            foreach (var addinDir in EnumerateServiceTierAddinDirs())
                if (Directory.Exists(addinDir))
                    probingPaths.Add(addinDir);
            // BCL: where mscorlib / System.* lives (shared framework) — last-resort
            // fallback for assemblies with no reference-assembly counterpart.
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            if (Directory.Exists(runtimeDir))
                probingPaths.Add(runtimeDir);

            if (Environment.GetEnvironmentVariable("BCCOMPILER_DIAG") == "1")
                Console.Error.WriteLine(
                    "[BcCompiler-diag] DotNet probing paths:\n  " + string.Join("\n  ", probingPaths));

            var locator = new NavDotNet.AssemblyLocator(probingPaths);
            _dotNetResolverFactory = new NavDotNet.DotNetResolverFactory(locator);
            return _dotNetResolverFactory;
        }
    }

    /// <summary>
    /// .NET reference-assembly directories (full-metadata, not forwarder facades) that BC's
    /// DotNet alias resolver can read TypeDefinitions from. Derived from the running dotnet
    /// install's <c>packs/</c> dir. Returns the NETStandard.Library.Ref (covers the
    /// <c>netstandard, 2.1.0.0</c> aliases the test-toolkit declares) and the
    /// Microsoft.NETCore.App.Ref pack (covers <c>System.Xml.*</c>, <c>System.Net.Http</c>,
    /// etc. used transitively). Highest version of each is preferred.
    /// </summary>
    private static IEnumerable<string> EnumerateDotNetRefAssemblyDirs()
    {
        // Resolve the dotnet root: shared-framework dir is …/dotnet/shared/Microsoft.NETCore.App/<v>.
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        string? dotnetRoot = null;
        var idx = runtimeDir.Replace('\\', '/').IndexOf("/shared/", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) dotnetRoot = runtimeDir.Substring(0, idx);
        dotnetRoot ??= Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot) || !Directory.Exists(dotnetRoot))
            yield break;

        var packs = Path.Combine(dotnetRoot, "packs");
        if (!Directory.Exists(packs)) yield break;

        // ORDER MATTERS. BC's DotNet alias resolver probes the path in order and binds a type
        // to the FIRST assembly that carries it as a real TypeDefinition. Both the net-core
        // ref pack's `System.Runtime.dll` and `netstandard.dll` carry `System.Uri` as a real
        // typedef, but Ncl's `ALAzureAdCodeGrantFlow` ctor references `System.Uri` from
        // `System.Runtime, 8.0.0.0`. If `netstandard, 2.1.0.0` (which also defines System.Uri)
        // is probed first, the toolkit's `DotNet Uri` alias binds to netstandard and the
        // conversion to Ncl's parameter fails (AL0133 "cannot convert System.Uri to System.Uri").
        // So yield the runtime-matched NETCore.App ref pack FIRST; netstandard only supplies the
        // handful of aliases (BindingFlags, etc.) that have no net-core ref-pack counterpart.

        // Microsoft.NETCore.App.Ref/<v>/ref/net<x>.0 — MUST match the runtime major version BC's
        // Ncl.dll was built against (BC 28 = .NET 8 → System.Runtime, 8.0.0.0), NOT simply the
        // highest pack installed (a net10 ref pack would bind System.Uri to System.Runtime,
        // 10.0.0.0 and break the same conversion). Pin to the running shared-framework major.
        var coreRef = Path.Combine(packs, "Microsoft.NETCore.App.Ref");
        if (Directory.Exists(coreRef))
        {
            int? runtimeMajor = null;
            var sfName = Path.GetFileName(runtimeDir.TrimEnd('/', '\\')); // "8.0.25"
            if (Version.TryParse(sfName, out var sfv)) runtimeMajor = sfv.Major;

            var candidates = Directory.EnumerateDirectories(coreRef)
                .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(t => t.Ver != null)
                .ToList();
            var best = candidates
                .Where(t => runtimeMajor == null || t.Ver!.Major == runtimeMajor.Value)
                .OrderByDescending(t => t.Ver)
                .Select(t => t.Dir)
                .FirstOrDefault()
                ?? candidates.OrderByDescending(t => t.Ver).Select(t => t.Dir).FirstOrDefault();
            if (best != null)
            {
                var refSub = Path.Combine(best, "ref");
                if (Directory.Exists(refSub))
                    foreach (var tfm in Directory.EnumerateDirectories(refSub))
                        yield return tfm;
            }
        }

        // NETStandard.Library.Ref/<v>/ref/netstandard2.1 — AFTER the net-core ref pack.
        var nsRef = Path.Combine(packs, "NETStandard.Library.Ref");
        if (Directory.Exists(nsRef))
        {
            var best = Directory.EnumerateDirectories(nsRef)
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
                .FirstOrDefault();
            if (best != null)
            {
                var refSub = Path.Combine(best, "ref");
                if (Directory.Exists(refSub))
                    foreach (var tfm in Directory.EnumerateDirectories(refSub))
                        yield return tfm;
            }
        }
    }

    /// <summary>
    /// BC ServiceTier <c>Add-ins/*</c> directories. Microsoft's test-toolkit declares
    /// <c>DotNet "Microsoft.Dynamics.Nav.PermissionTestHelper"</c>, whose DLL ships under
    /// <c>ServiceTier/.../Service/Add-ins/PermissionTestHelper/</c>, not in the flat
    /// artifacts dir. Surfacing the add-in dirs lets that alias resolve.
    /// </summary>
    private static IEnumerable<string> EnumerateServiceTierAddinDirs()
    {
        // ServiceTierDir is …/artifacts/<v>; the bcartifacts ServiceTier add-ins live in the
        // download cache. Probe both the artifacts dir's own Add-ins (if flattened there) and
        // the sibling bcartifacts cache.
        var roots = new List<string>();
        if (Directory.Exists(DefaultServiceTierDir))
            roots.Add(DefaultServiceTierDir);
        // bcartifacts cache: scope to the HIGHEST-version sandbox dir only. The cache can hold
        // several BC versions side by side (e.g. 27.5 and 28.1); adding all of them puts a
        // second copy of every Add-in / Test-Assembly (incl. MockTest.dll) on the DotNet
        // probing path, and the AssemblyLocator may then bind a type (e.g.
        // MockTest.MockAzureKeyVaultSecretProvider) from the WRONG-version assembly whose
        // transitive Ncl reference does not match the pinned-version Ncl on the path → the
        // toolkit's Azure-KV / Azure-AD codeunits fail to bind the interface (AL0133). Scope
        // to the pinned version (mirrors BcArtifacts' highest-version convention).
        // AlRunner.Infrastructure.AlRunnerPaths.UserHome throws loudly (issue #2114) rather
        // than silently handing back a relative path when $HOME names a directory that
        // does not exist.
        var sandbox = Path.Combine(
            AlRunner.Infrastructure.AlRunnerPaths.UserHome, ".bcartifacts.cache", "sandbox");
        if (Directory.Exists(sandbox))
        {
            var bestSandbox = Directory.EnumerateDirectories(sandbox)
                .Select(d => (Dir: d, Ver: System.Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(t => t.Ver != null)
                .OrderByDescending(t => t.Ver)
                .Select(t => t.Dir)
                .FirstOrDefault();
            if (bestSandbox != null) roots.Add(bestSandbox);
        }
        foreach (var root in roots)
        {
            // ServiceTier Add-ins/* (PermissionTestHelper et al.).
            IEnumerable<string> addins;
            try { addins = Directory.EnumerateDirectories(root, "Add-ins", SearchOption.AllDirectories); }
            catch { addins = Array.Empty<string>(); }
            foreach (var addinRoot in addins)
            {
                yield return addinRoot;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(addinRoot); }
                catch { continue; }
                foreach (var sub in subs) yield return sub;
            }
            // Test Assemblies/* (MockTest.dll — in-process test mocks such as the mock
            // Azure Key Vault secret provider the System App Test Library aliases).
            IEnumerable<string> testAsm;
            try { testAsm = Directory.EnumerateDirectories(root, "Test Assemblies", SearchOption.AllDirectories); }
            catch { testAsm = Array.Empty<string>(); }
            foreach (var taRoot in testAsm)
            {
                yield return taRoot;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(taRoot); }
                catch { continue; }
                foreach (var sub in subs) yield return sub;
            }
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
    // Process-wide staging dir for deduplicated .app symlinks (one subdir per loader signature).
    private static readonly object _stageSync = new();
    private static string? _stageRootCache;

    /// <summary>
    /// One .app package the loader's scan set ended up containing, in scan order. This is
    /// the exact candidate list BC's own <c>AbstractSymbolReferenceAnalyzer</c> would build
    /// from the returned dirs, so it is what <see cref="SelfExcludingSymbolReferenceLoader"/>
    /// reasons about when it decides whether hiding one AppId is equivalent to physically
    /// deleting its .app from the scan set.
    /// </summary>
    internal readonly record struct PackageScanEntry(
        string Path, Guid AppId, string Publisher, string Name, Version Version);

    /// <summary>
    /// One .app that <see cref="DeduplicateAppPackageDirs"/> left OUT of the staging dir
    /// because it carries no <c>SymbolReference.json</c>, so it cannot supply compile-time
    /// symbols to anything. See the drop site for why keeping it would fail every compile
    /// that scans its directory.
    /// </summary>
    internal readonly record struct UnstagedPackage(string Path, string Publisher, string Name, Version Version);

    /// <summary>
    /// What a deduplicated staging directory stands for: the original package dirs it was
    /// built from, and the packages that were dropped on the way in.
    ///
    /// #2178 (and #2108 before it): BC's own AL1022 text names the folders it searched, and
    /// once the scan set has been collapsed that is one content-addressed path under
    /// <c>al-runner-pkgdedup/</c>. The path is honest — it really is what BC searched — but
    /// on its own it tells the reader nothing about which of their <c>--package-cache</c>
    /// directories it represents, and nothing about a package that WAS in one of them and
    /// was excluded here. <see cref="DescribeStagedSearchSet"/> turns it back into both.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, (IReadOnlyList<string> Origin, IReadOnlyList<UnstagedPackage> Unstaged)> _stageProvenance =
        new(StringComparer.Ordinal);

    /// <summary>
    /// An explanatory suffix for a compiler diagnostic that names a pkgdedup staging
    /// directory, or null when it names none. Never speculates: it reports the dirs the
    /// staging copy was actually built from and the packages actually dropped from it.
    /// </summary>
    internal static string? DescribeStagedSearchSet(string diagnosticText)
    {
        foreach (var kv in _stageProvenance)
        {
            if (!diagnosticText.Contains(kv.Key, StringComparison.Ordinal)) continue;
            var sb = new System.Text.StringBuilder();
            sb.Append(" [al-runner] That folder is a deduplicated staging copy of: ")
              .Append(string.Join(", ", kv.Value.Origin))
              .Append('.');
            if (kv.Value.Unstaged.Count > 0)
            {
                sb.Append(" Excluded from it, because they carry no SymbolReference.json and so cannot supply compile-time symbols: ")
                  .Append(string.Join(", ", kv.Value.Unstaged.Take(10)
                      .Select(u => $"{u.Publisher}/{u.Name} {u.Version} ({u.Path})")));
                if (kv.Value.Unstaged.Count > 10)
                    sb.Append($", and {kv.Value.Unstaged.Count - 10} more");
                sb.Append('.');
            }
            return sb.ToString();
        }
        return null;
    }

    // Per-file cache of the two zip reads DeduplicateAppPackageDirs performs on every .app
    // it scans (ReadManifest + HasSymbolReference). Both re-read the WHOLE package from
    // disk and unzip it — for a 113-package, 138 MB platform-apps dir that is ~1–2.5 s per
    // scan, and the scan runs on EVERY GetSharedReferences call (it has to: its output is
    // what the loader signature is computed from). Keyed by path + length + last-write
    // ticks, so a package rewritten in place (InProcessAppPackager's synthetic .apps, a
    // --watch rebuild) invalidates its own entry.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, (long Length, long Ticks, AppManifest? Manifest, bool HasSymbolReference)> _appMetaCache = new();

    private static (AppManifest? Manifest, bool HasSymbolReference) ReadAppMeta(FileInfo fi)
    {
        var path = fi.FullName;
        long len, ticks;
        try { len = fi.Length; ticks = fi.LastWriteTimeUtc.Ticks; }
        catch { return (AppLoader.ReadManifest(path), AppLoader.HasSymbolReference(path)); }

        if (_appMetaCache.TryGetValue(path, out var hit) && hit.Length == len && hit.Ticks == ticks)
            return (hit.Manifest, hit.HasSymbolReference);

        var meta = (AppLoader.ReadManifest(path), AppLoader.HasSymbolReference(path));
        _appMetaCache[path] = (len, ticks, meta.Item1, meta.Item2);
        return meta;
    }

    /// <summary>
    /// Test seam: forget every cached reference loader, its signature, the scan-metadata
    /// cache and the build counter, so a test can observe rebuild behaviour from a known
    /// zero. Not used by the runner itself — the caches are process-lifetime by design.
    /// </summary>
    internal static void ResetSharedReferencesForTests()
    {
        lock (_refSync)
        {
            _refLoader = null;
            _refPackageLoader = null;
            _loaderSignature = null;
            _loaderDepUniverse = null;
            _warmSpecCount = 0;
            _exclPackageLoader = null;
            _exclSignature = null;
            _exclDepUniverse = null;
            _cachedJsonLoaders = null;
            _refSpecs = null;
            _siblingSymbols = null;
            _extraSymbolDirs = null;
            _resolvedDeps = null;
            _packageCacheDirs = null;
            _currentAppId = null;
            _currentPublisher = null;
            _currentVersion = null;
            _usePackageCacheFallback = false;
            _packageCacheFallbackExcludeId = default;
            _loaderBuildCount = 0;
        }
        _appMetaCache.Clear();
    }

    /// <summary>
    /// Test seam: pin <c>_packageCacheDirs</c> to an explicit (possibly empty) list so
    /// <see cref="GetSharedReferences"/> never falls through to <see cref="ResolveSymbolDirs"/>
    /// (issue #1992). That fallback reads the CALLING MACHINE's real, process-wide symbol
    /// caches — <c>~/.local/share/al-runner/symbols/&lt;ver&gt;</c> and
    /// <c>~/.bcartifacts.cache/sandbox/&lt;ver&gt;/{w1/Extensions,platform/Applications}</c> —
    /// which is exactly the behaviour <see cref="SetResolvedDeps"/> gives the real compile
    /// pipeline, but tests that exercise the memo directly (bypassing SetResolvedDeps) got it
    /// only by accident of whichever dirs happen to exist on whoever's machine runs them.
    ///
    /// A test asserting an EXACT rebuild count is reading BcCompiler's memo signature, which
    /// folds in every scanned package dir (see ComputeLoaderSignature/DeduplicateAppPackageDirs).
    /// On a dev box with a populated `.bcartifacts.cache/sandbox` (hundreds of real .app
    /// files, some already deduped against each other), DeduplicateAppPackageDirs' "changed"
    /// flag can already be true before the test's own fixture contributes anything — so the
    /// FIRST call already takes the content-addressed staging path instead of the
    /// unchanged-dirs fast path the test's math assumes as its baseline. A later call whose
    /// only real-world change is a test-fixture duplicate (deduped away to the SAME surviving
    /// file set) then hashes to the SAME staging key as the first call — the loader legitimately
    /// serves the same modules, so this isn't wrong on the merits, but it defeats the test's
    /// contract of forcing a rebuild through a specific narrow mechanism. Scoping
    /// `_packageCacheDirs` here removes that machine-state coupling entirely: these tests then
    /// see ONLY the dirs they construct, on every machine, deterministically.
    /// </summary>
    internal static void SetPackageCacheDirsForTests(IReadOnlyList<string> dirs)
    {
        lock (_refSync) { _packageCacheDirs = dirs; }
    }

    private static List<string> DeduplicateAppPackageDirs(List<string> packageDirs, Guid? excludeAppId = null)
        => DeduplicateAppPackageDirs(packageDirs, excludeAppId, out _);

    /// <summary>
    /// Returns a package-dir list in which each app identity (AppId) appears at most once,
    /// and — when <paramref name="excludeAppId"/> is set — in which that one AppId is absent
    /// entirely. If neither a cross-dir duplicate nor the excluded AppId is found, the
    /// original list is returned unchanged (zero cost, byte-identical loader behaviour).
    /// Otherwise a staging directory is built containing one symlink (copy fallback) per
    /// unique, non-excluded AppId — keeping the first occurrence in scan order — and a
    /// single-element list pointing at it is returned. Non-.app content (e.g.
    /// *.symbols.json) is intentionally NOT staged here; the caller scans the ORIGINAL dirs
    /// for those. See call site for the AL0275 rationale.
    /// <paramref name="inventory"/> receives the surviving packages in scan order.
    /// </summary>
    private static List<string> DeduplicateAppPackageDirs(
        List<string> packageDirs, Guid? excludeAppId, out List<PackageScanEntry> inventory)
    {
        // Collect every .app, keyed by (AppId, Version), preserving dir scan order. The key
        // MUST include the version: the same AppId legitimately ships in multiple versions
        // across the package caches, and the resolver needs each distinct version to satisfy a
        // version-pinned reference (collapsing by AppId alone drops versions -> AL1022). A
        // second occurrence of the SAME (AppId, Version) is dropped -- that is the byte-for-byte
        // duplicate (e.g. a test-library .app present in both the bundle .alpackages and the
        // bcartifacts test dir) that produces the AL0275 self-ambiguity.
        //
        // excludeAppId additionally drops EVERY occurrence of one specific AppId outright.
        // This is used when compiling a dependency's OWN decompiled AL source as the PRIMARY
        // compile (DependencyLoader's Tier-3 path): GetSharedReferences already excludes that
        // dep's own SymbolReferenceSpecification (via _currentAppId) so the compiler never
        // REQUESTS it as a reference — but the reference LOADER is a separate object built
        // from a directory scan, and it still happily enumerates + serves that dep's own .app
        // if the .app is simply present in one of the scanned dirs (which it always is, since
        // that's exactly how DependencyResolver found it in the first place). BC's binder
        // resolves loosely-typed references (e.g. a Permission Set's `tabledata "X"` grant)
        // by asking the loader for ANY module that declares "X" — regardless of whether a
        // spec was ever added for that module — so the same table ends up visible via BOTH
        // the primary source tree being compiled AND the still-loader-visible .app, and BC
        // reports "'X' is an ambiguous reference between ... and ..." naming the SAME
        // extension twice. Physically removing the .app from the loader's scan set (not just
        // from the requested specs) is the only way to make that dep's own source the sole
        // source of truth for its own objects during its own compile.
        var seen = new HashSet<(Guid, string)>();
        var picked = new List<string>();
        inventory = new List<PackageScanEntry>();
        // #2178: remembered, not just counted. When the staging dir is what BC ends up
        // naming in an AL1022 "could not be found in the package cache folders" message,
        // a package that was IN one of the caller's dirs and dropped here is precisely the
        // thing the reader needs told. See DescribeStagedSearchSet.
        var unstaged = new List<UnstagedPackage>();
        var changed = false;
        foreach (var dir in packageDirs)
        {
            IEnumerable<FileInfo> apps;
            try { apps = new DirectoryInfo(dir).EnumerateFiles("*.app", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var appInfo in apps)
            {
                var app = appInfo.FullName;
                var (m, hasSymbolReference) = ReadAppMeta(appInfo);
                if (m == null) continue;
                if (excludeAppId != null && m.AppId == excludeAppId.Value) { changed = true; continue; }
                if (!seen.Add((m.AppId, m.Version.ToString()))) { changed = true; continue; }
                // Drop packages carrying no SymbolReference.json. The native .app scanner
                // cannot serve them — it reports AL1023 "package file is not valid" — and the
                // error is attributed to the COMPILATION, not to the package, so a single such
                // file fails every compile that scans its directory even when nothing
                // references it. That is a bundle-wide blast radius from one unrelated suite's
                // fixture.
                //
                // Removing them loses nothing: a symbol-less .app is either a synthetic
                // source-dependency package (its symbols reach the compiler through the
                // *.symbols.json JSON loader chain below, which is the intended route) or a
                // fixture that exists purely for DependencyResolver's identity lookup, which
                // reads manifests directly and never goes through this scan set.
                //
                // ScopeSymbolBearingDepsOnly applies the same rule to the RESOLVED DEP list;
                // this is its counterpart for the directory scan. Both are needed — the two
                // paths reach the compiler independently, and BC 27 is far stricter than BC 28
                // about a malformed package, so a gap here shows up as a version-specific
                // failure that looks like a runner capability gap and is not one.
                if (!hasSymbolReference)
                {
                    changed = true;
                    unstaged.Add(new UnstagedPackage(
                        Path.GetFullPath(app), m.Publisher, m.Name, m.Version));
                    continue;
                }
                // Normalise to an absolute path BEFORE it is ever used as a symlink target.
                // `dir` (and therefore `app`) may be a caller-supplied RELATIVE path (e.g. a
                // relative --package-cache argument, exactly as in issue #1652's repro:
                // `--package-cache app/.alpackages`). File.CreateSymbolicLink below treats its
                // target argument LITERALLY — a relative target is resolved by the OS relative
                // to the SYMLINK's own directory (the temp stage dir under
                // al-runner-pkgdedup/<hash>/), not relative to this process's CWD. Staging a
                // relative target therefore produces a DANGLING symlink: BC's native package
                // reader then reports the (perfectly valid) package as `AL1023 ... is not
                // valid`, the resulting declaration errors cascade into Compilation.Emit, and
                // the emitter crashes on unbound types — the EMIT-ZERO failure this dedup path
                // is supposed to prevent, not cause. Resolving to an absolute path here makes
                // every downstream symlink target valid regardless of CWD.
                var full = Path.GetFullPath(app);
                picked.Add(full);
                inventory.Add(new PackageScanEntry(full, m.AppId, m.Publisher, m.Name, m.Version));
            }
        }
        if (!changed) return packageDirs; // common case — leave the hot path untouched

        // Build (or reuse) a staging dir keyed by the exact picked-app set so concurrent /
        // repeated compiles with the same dedup/exclusion result share one staging dir.
        var sb = new System.Text.StringBuilder();
        foreach (var p in picked.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(p).Append('\n');
        string key;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            key = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))
                .ToLowerInvariant()[..16];

        lock (_stageSync)
        {
            _stageRootCache ??= Path.Combine(Path.GetTempPath(), "al-runner-pkgdedup");
            var stage = Path.Combine(_stageRootCache, key);
            if (!Directory.Exists(stage))
            {
                var tmp = stage + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                Directory.CreateDirectory(tmp);
                foreach (var src in picked)
                {
                    var dst = Path.Combine(tmp, Path.GetFileName(src));
                    // Two different apps can share a file name across dirs (rare); disambiguate.
                    if (File.Exists(dst))
                        dst = Path.Combine(tmp, Path.GetFileNameWithoutExtension(src) + "_" +
                                                Guid.NewGuid().ToString("N")[..6] + ".app");
                    try { File.CreateSymbolicLink(dst, src); }
                    catch { try { File.Copy(src, dst, overwrite: true); } catch { } }
                }
                // Publishing the scratch dir under its content-addressed name is best-effort:
                // on Windows the move of a just-written tree intermittently hits a transient
                // "Access to the path ... is denied", and rethrowing there killed whole runs
                // at emit time (#1691). Publish retries, then falls back to the scratch dir —
                // same staged files, so the compile is unaffected; only the cross-compile
                // reuse of this key is lost.
                var published = AlRunner.Infrastructure.PkgDedupStaging.Publish(tmp, stage, Console.Error);
                _stageProvenance[published] = (packageDirs.ToList(), unstaged);
                return new List<string> { published };
            }
            _stageProvenance[stage] = (packageDirs.ToList(), unstaged);
            return new List<string> { stage };
        }
    }

    /// <summary>
    /// The content signature of the inputs a BC reference loader is actually CONSTRUCTED
    /// from: the dirs it scans, plus the extra <c>*.symbols.json</c> dirs chained ahead of
    /// it (and, for the rare physically-reduced fallback, the excluded AppId).
    ///
    /// #1832: the resolved dep set used to be folded in here as <c>D:</c> lines, i.e.
    /// compared for equality. It moved to <see cref="_loaderDepUniverse"/>, which compares
    /// it as a SUBSET — see there for why removing a dep cannot change a loader's answers
    /// and why adding or changing one still must.
    /// </summary>
    private static string ComputeLoaderSignature(
        List<string> packageDirs,
        IReadOnlyList<string>? extraSymbolDirs,
        Guid? excludeAppId)
    {
        var parts = new List<string>();
        foreach (var d in packageDirs.OrderBy(x => x, StringComparer.Ordinal)) parts.Add("P:" + d);
        if (extraSymbolDirs != null)
            foreach (var d in extraSymbolDirs.OrderBy(x => x, StringComparer.Ordinal)) parts.Add("X:" + d);
        // NOTE: the excluded-self-app is deliberately NOT part of the signature. The
        // packageDirs passed here are already DeduplicateAppPackageDirs' OUTPUT, which
        // fully encodes the exclusion: when the excluded AppId is actually present in the
        // scan set the result is a staging dir whose name is a hash of the exact picked-app
        // set (so dep A's and dep B's loaders get different signatures), and when it is
        // absent the input dirs are returned unchanged (so the loader really is identical).
        //
        // Signing on the raw AppId instead made the signature change on every identity
        // switch even when the scan set did not, which rebuilt the expensive loader
        // (filesystem scan + symbol warm, ~800ms) once per app. That was invisible while a
        // bundle compiled as a single module; emitting one module per app.json made it fire
        // 68 times on tests/runner-extras and took the run from 23s to 110s.
        //
        // Since #1831 the PRIMARY loader is built from the exclusion-free SUPERSET scan set
        // (excludeAppId == null), so its signature no longer varies per dependency compile
        // and one warm loader serves them all; the per-compile exclusion is applied by
        // SelfExcludingSymbolReferenceLoader on top. excludeAppId is still passed (and still
        // folded in) for the rare fallback loader built when hiding is NOT provably
        // equivalent to physically dropping the .app — that one really is per-exclusion.
        if (excludeAppId != null) parts.Add("E:" + excludeAppId.Value.ToString("N"));
        return string.Join("\n", parts);
    }

    private static (NavCA.ISymbolReferenceLoader? Loader, NavCA.SymbolReferenceSpecification[] Specs)
        GetSharedReferences(IEnumerable<string> bundleAlpackagesDirs)
    {
        lock (_refSync)
        {
            // BCCOMPILER_TIMING=1 breaks this method's cost into its four parts. The whole
            // point of #1831 is that only the first (dedup-scan) runs on a memo HIT; if a
            // profile ever shows `create-loader`/`warm` repeating per dependency again, the
            // memo key has drifted back to something exclusion-dependent.
            bool timing = Environment.GetEnvironmentVariable("BCCOMPILER_TIMING") == "1";
            var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string phase)
            {
                if (timing) Console.Error.WriteLine($"[shared-refs] {phase}: {phaseWatch.ElapsedMilliseconds}ms");
                phaseWatch.Restart();
            }
            // ── Loader (expensive filesystem scan + symbol warm) — cache and reuse ──
            // The loader scans package dirs for .app files and serves ModuleDefinitions,
            // then WarmReferenceLoader sequentially reads every reachable symbol spec
            // (~40s for a heavy Base App dep set). This is pure dependency work — it does
            // not depend on the bundle source — so it is rebuilt ONLY when its content
            // signature (package dirs + extra symbol dirs + resolved dep set + the excluded
            // self-app, see below) changes. Unchanged deps → the warm loader is reused
            // across calls, across bundles, and across --watch re-runs.
            var packageDirs = bundleAlpackagesDirs
                .Where(Directory.Exists)
                .Distinct()
                .ToList();
            if (_packageCacheDirs != null)
                packageDirs.AddRange(_packageCacheDirs.Where(Directory.Exists));
            else
                packageDirs.AddRange(ResolveSymbolDirs());
            packageDirs = packageDirs.Distinct().ToList();

            // Deduplicate the .app set the symbol loader sees BY APP IDENTITY, AND exclude
            // the AppId currently being compiled as PRIMARY source (_currentAppId), if any.
            //
            // Dedup: the same Microsoft app (e.g. "System Application Test Library") is
            // commonly present in BOTH the bundle's own .alpackages AND the bcartifacts
            // test-app cache dir we add for test-toolkit resolution. CreateReferenceLoader
            // scans every dir, so it loads the identical module twice → BC binds it as two
            // extensions with the same name → AL0275 "ambiguous reference between X (…) and
            // X (…)" for every type it declares.
            //
            // Self-exclusion: when a dependency's OWN decompiled AL source is the PRIMARY
            // compile (DependencyLoader's Tier-3 path scopes _currentAppId to that dep's own
            // identity via ScopeCurrentAppIdentity), the SPEC list below already excludes
            // that dep's own SymbolReferenceSpecification — but the reference LOADER is a
            // separate object built from a directory scan and still happily serves that
            // dep's .app if it's simply present in a scanned dir (which it always is: that
            // .app is exactly how DependencyResolver found the dep in the first place). BC's
            // binder resolves some references (e.g. a Permission Set's `tabledata "X"` grant)
            // by asking the loader for ANY module declaring "X", regardless of whether a spec
            // was ever added for that module — so the object ends up visible via BOTH the
            // primary source tree being compiled AND the still-loader-visible .app, and BC
            // reports the exact "'X' is an ambiguous reference between ... and ..." (same
            // extension named twice) failure this fixes. Physically removing that one .app
            // from the loader's scan set — not just from the requested specs — makes the
            // dep's own source the sole source of truth for its own objects during its own
            // compile. Staging one .app per unique (non-excluded) AppId is a no-op when there
            // is nothing to dedup/exclude, so the corpus/main-bundle path (which never needs
            // self-exclusion) keeps identical behaviour and cost.
            //
            // #1831: the exclusion is NOT applied to this scan any more. Applying it here
            // makes the scan-dir set — and therefore the loader signature — differ for every
            // dependency compiled from source, so the single memo slot missed every time and
            // the loader's per-instance symbol warm (8–10 s on the Microsoft test-library dep
            // set) was paid once per dependency: 8 × ~11.5 s ≈ 92 s of a cold runner-extras
            // leg. The loader is now built ONCE from the exclusion-free SUPERSET, and the
            // per-compile exclusion is applied on top by SelfExcludingSymbolReferenceLoader,
            // which refuses to answer for the excluded AppId using BC's own IsSatisfiedBy
            // predicate. See that class for why hiding == physically dropping, and for the
            // one case (a surviving package sharing the excluded app's Name) where it is not
            // provably so and this code falls back to a physically-reduced loader.
            var loaderScanDirs = DeduplicateAppPackageDirs(packageDirs, null, out var scanInventory);
            Mark($"dedup-scan ({scanInventory.Count} pkgs)");

            var loaderSig = ComputeLoaderSignature(loaderScanDirs, _extraSymbolDirs, null);
            var depKeys = DepUniverseKeys(_resolvedDeps);
            if (_refLoader == null || loaderSig != _loaderSignature
                || !depKeys.IsSubsetOf(_loaderDepUniverse ?? new HashSet<string>(StringComparer.Ordinal)))
            {
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
                //
                // This block is built BEFORE the .app scanner and independently of it: with
                // no package-cache dir at all (a bundle whose only dependency is a SIBLING
                // SOURCE app — no .alpackages, no ~/.bcartifacts.cache, no provisioned
                // test-apps/platform-apps dir; exactly CI's `package caches (requested): 0
                // dir(s)`), the old `loaderScanDirs.Count == 0` early-return bailed out here and the
                // source dep's freshly written *.symbols.json was never consulted. The dep
                // loaded fine at RUNTIME and was invisible at COMPILE time — AL0185
                // "Codeunit 'X' is missing", after which BC's emitter crashes on the
                // now-unresolved local variable type ("Unexpected value 'None' of type
                // NavTypeKind") and the whole bundle emits zero sources.
                var jsonScanDirs = packageDirs.ToList();
                if (_extraSymbolDirs != null)
                    foreach (var d in _extraSymbolDirs)
                        if (Directory.Exists(d) && !jsonScanDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                            jsonScanDirs.Add(d);

                var jsonLoaders = jsonScanDirs
                    .Select(d => new JsonSymbolReferenceLoader(d))
                    .Where(l => l.HasAny)
                    .ToList();
                Mark("json-loaders");

                // Nothing to serve references from at all — same no-op result as before.
                // (Deliberately leaves _refLoader / _loaderSignature untouched, as the
                // original early-return did.)
                if (loaderScanDirs.Count == 0 && jsonLoaders.Count == 0)
                    return (null, Array.Empty<NavCA.SymbolReferenceSpecification>());

                _cachedJsonLoaders = jsonLoaders;
                NavCA.ISymbolReferenceLoader? packageLoader = loaderScanDirs.Count > 0
                    ? NavSymRef.ReferenceLoaderFactory.CreateReferenceLoader(loaderScanDirs)
                    : null;
                _refPackageLoader = packageLoader;
                if (jsonLoaders.Count > 0)
                {
                    var chain = jsonLoaders.Cast<NavCA.ISymbolReferenceLoader>().ToList();
                    if (packageLoader != null) chain.Add(packageLoader);
                    _refLoader = new CompositeSymbolReferenceLoader(chain);
                }
                else
                {
                    _refLoader = packageLoader!;
                }
                _loaderBuildCount++;

                // Pre-warm the loader's internal package caches SEQUENTIALLY before the
                // compiler's parallel reference-loading runs. BC's ReferenceManager fans
                // GetDependencies out across ThreadPool workers; concurrent first-reads of
                // the same R2R .app race inside NavAppPackageReader.CreateEmbeddedReader and
                // wedge in an unbounded Stream.CopyTo (intermittent compile hang on bundles
                // that pull MS test-library deps — proven gone when the process is pinned to
                // one CPU). Warming every reachable spec here makes that later parallel phase
                // hit warm caches instead of racing on cold file reads. Best-effort: any
                // failure just leaves the cold-read path to the compiler as before.
                Mark("create-loader");
                WarmReferenceLoader(_refLoader, _resolvedDeps);
                Mark("warm");
                _loaderSignature = loaderSig;
                // This instance is warm for exactly these dep keys; a later call whose deps
                // are a subset of them reuses it (#1832).
                _loaderDepUniverse = depKeys;
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
            // ── Per-compile self-exclusion of the PACKAGE loader (#1831) ───────────────
            // The cached loader was built from the exclusion-free superset. When this
            // compile is a dependency compiling its own decompiled AL source, that dep's
            // own .app must not be reachable through the loader (AL0275 self-ambiguity —
            // see SelfExcludingSymbolReferenceLoader and BcCompilerLoaderSelfExclusionTests).
            //
            // The decorator wraps ONLY the package loader and stays at the END of the chain,
            // exactly where the physically-reduced package loader sat: the JSON-symbols
            // loaders ahead of it were never affected by the .app-level exclusion (their own
            // self-exclusion is applied to the SPECS above), and a null answer from the last
            // child is what a reduced BC loader returns for a package it cannot find.
            NavCA.ISymbolReferenceLoader? effectiveLoader = _refLoader;
            if (_currentAppId is Guid selfAppId && _refPackageLoader != null)
            {
                var hidden = scanInventory
                    .Where(e => e.AppId == selfAppId)
                    .Select(e => (e.Publisher, e.Name, e.AppId, e.Version))
                    .ToList();
                if (hidden.Count > 0)
                {
                    NavCA.ISymbolReferenceLoader excludedPackageLoader;
                    if (SelfExcludingSymbolReferenceLoader.CanHideInsteadOfRescan(
                            scanInventory.Select(e => (e.AppId, e.Name)).ToList(), selfAppId))
                    {
                        excludedPackageLoader =
                            new SelfExcludingSymbolReferenceLoader(_refPackageLoader, hidden);
                    }
                    else
                    {
                        // Not provably equivalent: some surviving package shares the excluded
                        // app's Name (or an AppId is empty), so deleting the .app could promote
                        // that survivor to winner where hiding would answer "not found". Do
                        // what the runner did before #1831 — a loader over a physically reduced
                        // scan set, memoised in its own slot so repeats within a run are free.
                        excludedPackageLoader = GetPhysicallyExcludedPackageLoader(packageDirs, selfAppId);
                        Mark($"excluded-loader rebuild (name collision, excl={selfAppId})");
                    }

                    var chain = new List<NavCA.ISymbolReferenceLoader>();
                    if (_cachedJsonLoaders != null)
                        chain.AddRange(_cachedJsonLoaders.Cast<NavCA.ISymbolReferenceLoader>());
                    chain.Add(excludedPackageLoader);
                    effectiveLoader = chain.Count == 1
                        ? chain[0]
                        : new CompositeSymbolReferenceLoader(chain);
                }
            }

            // Siblings emitted earlier in this bundle. Chained ahead of the cached loader
            // for THIS call only — never stored as _refLoader and never folded into the
            // loader signature, so a sibling appearing mid-bundle costs a dictionary lookup
            // rather than a rebuild + symbol warm. The same self-exclusion applies: an app
            // must not reference its own symbols alongside its own source (AL0275).
            if (_siblingSymbols != null && _siblingSymbols.HasAny)
            {
                var siblingSpecs = _siblingSymbols.EnumerateSpecs()
                    .Where(s => _currentAppId == null || s.AppId != _currentAppId.Value)
                    .ToList();
                if (siblingSpecs.Count > 0)
                {
                    var have = new HashSet<Guid>(specs.Select(s => s.AppId));
                    var extra = siblingSpecs
                        .Where(s => have.Add(s.AppId))
                        .Select(s => new NavCA.SymbolReferenceSpecification(
                            publisher: s.Publisher, name: s.Name, version: s.Version,
                            exact: false, appId: s.AppId, isPropagated: false,
                            alternateIds: ImmutableArray<Guid>.Empty))
                        .ToArray();
                    if (extra.Length > 0) specs = specs.Concat(extra).ToArray();
                }
                effectiveLoader = effectiveLoader == null
                    ? _siblingSymbols
                    : new CompositeSymbolReferenceLoader(
                        new List<NavCA.ISymbolReferenceLoader> { _siblingSymbols, effectiveLoader });
            }

            _refSpecs = specs; // keep for any legacy callers that read _refSpecs directly
            return (effectiveLoader, specs);
        }
    }

    /// <summary>
    /// The pre-#1831 path: a package loader over a scan set from which every <c>.app</c> of
    /// <paramref name="excludeAppId"/> has been physically removed, warmed like the shared
    /// one. Only reached when hiding is not provably equivalent to removing (see
    /// <see cref="SelfExcludingSymbolReferenceLoader.CanHideInsteadOfRescan"/>). Memoised in
    /// its own single slot so a repeat of the same exclusion inside one run is free.
    /// Caller holds <see cref="_refSync"/>.
    /// </summary>
    private static NavCA.ISymbolReferenceLoader GetPhysicallyExcludedPackageLoader(
        List<string> packageDirs, Guid excludeAppId)
    {
        var reducedDirs = DeduplicateAppPackageDirs(packageDirs, excludeAppId);
        // Same #1832 rule as the shared loader: reducedDirs (which already encode the
        // exclusion) are compared for equality, the dep set for subset.
        var sig = ComputeLoaderSignature(reducedDirs, _extraSymbolDirs, excludeAppId);
        var depKeys = DepUniverseKeys(_resolvedDeps);
        if (_exclPackageLoader != null && sig == _exclSignature
            && depKeys.IsSubsetOf(_exclDepUniverse ?? new HashSet<string>(StringComparer.Ordinal)))
            return _exclPackageLoader;

        _exclPackageLoader = NavSymRef.ReferenceLoaderFactory.CreateReferenceLoader(reducedDirs);
        _exclSignature = sig;
        _loaderBuildCount++;
        WarmReferenceLoader(_exclPackageLoader, _resolvedDeps);
        _exclDepUniverse = depKeys;
        return _exclPackageLoader;
    }

    /// <summary>
    /// The identity of a resolved dep set as far as a reference loader is concerned:
    /// <c>AppPath@Version</c> per dep — exactly the <c>D:</c> lines the pre-#1832 loader
    /// signature carried, so "this key changed" means what it always meant. Compared as a
    /// SUBSET rather than for equality; see <see cref="_loaderDepUniverse"/>.
    /// </summary>
    private static HashSet<string> DepUniverseKeys(
        IReadOnlyList<(AppManifest Manifest, string AppPath)>? deps)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (deps == null) return keys;
        foreach (var d in deps) keys.Add(d.AppPath + "@" + d.Manifest.Version);
        return keys;
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
            var alreadyWarmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<NavCA.SymbolReferenceSpecification>();
            foreach (var d in resolvedDeps)
                queue.Enqueue(new NavCA.SymbolReferenceSpecification(
                    publisher: d.Manifest.Publisher, name: d.Manifest.Name, version: d.Manifest.Version,
                    exact: false, appId: d.Manifest.AppId, isPropagated: false,
                    alternateIds: ImmutableArray<Guid>.Empty));

            while (queue.Count > 0)
            {
                var spec = queue.Dequeue();
                if (!alreadyWarmed.Add($"{spec.Publisher}|{spec.Name}|{spec.Version}")) continue;
                _warmSpecCount++;
                IEnumerable<NavCA.SymbolReferenceSpecification>? deps = null;
                try { deps = loader.GetDependencies(spec, new List<NavCA.Diagnostics.Diagnostic>()); }
                catch { /* best-effort warm */ }
                if (deps == null) continue;
                foreach (var dep in deps) queue.Enqueue(dep);
            }
        }
        catch { /* best-effort warm — never block compilation */ }
    }

    /// <param name="appRootDir">
    /// The directory containing this app's own app.json — NOT <paramref name="alFolders"/>
    /// (which is typically the src/ subdirectory). BC's compiler needs an <c>IFileSystem</c>
    /// to resolve ControlAddIn resource paths (<c>Scripts</c>, <c>StartupScript</c>,
    /// <c>StyleSheets</c>, <c>Images</c>) — those are declared relative to the app root, e.g.
    /// <c>src/addin/startup.js</c>, not relative to the src/ folder itself. Without a file
    /// system, BC cannot resolve ANY such path and raises AL0327 "Missing file" for every
    /// declaration, even when the file is present at the declared path — see issue #1899.
    /// Null is accepted (skips WithFileSystem entirely) for callers that don't have a known
    /// app root, e.g. dependency compiles staging synthetic AL into a temp dir with no
    /// resource files anyway.
    /// </param>
    /// <param name="trackIncrementalBaseline">
    /// When true and this Emit succeeds cleanly (no excluded objects), record a RAD baseline
    /// for <paramref name="moduleName"/> so a LATER call to <see cref="TryEmitIncremental"/> can
    /// recompile a single changed file's worth of work instead of the whole module (issue
    /// #1902). Only <c>--watch</c> passes true — every other caller keeps Emit's existing cost
    /// profile unchanged (the extra ModuleDefinition conversion + file hashing this does is
    /// wasted work for a one-shot run that will never call TryEmitIncremental).
    /// </param>
    public BcEmitOutput Emit(
        IEnumerable<string> alFolders, string moduleName, string? appRootDir = null,
        bool trackIncrementalBaseline = false)
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

        // #1940/#1941/#1943: read the owning app's own manifest for the properties that
        // feed ParseOptions/CompilationOptions below. Prefer appRootDir (the documented
        // "real" app root — see the doc comment above); fall back to scanning `dirs` the
        // way EmitDepSymbols always has, for callers that don't have a separate app root
        // (appRootDir is null but one of alFolders already IS the app root).
        var manifestAppJsonPath = (appRootDir != null && File.Exists(Path.Combine(appRootDir, "app.json")))
            ? Path.Combine(appRootDir, "app.json")
            : dirs.Select(d => Path.Combine(d, "app.json")).FirstOrDefault(File.Exists);
        var manifestInputs = ReadManifestCompilerInputs(manifestAppJsonPath);

        // Preprocessor symbols: CLEANSCHEMA1..25 merged with any caller-supplied symbols
        // (--define / --preprocessor-symbols) AND the app's own manifest `preprocessorSymbols`
        // (#1943) — union, not override, so a manifest symbol never silently loses to a CLI
        // one or vice versa. v1 computes per-source max from any #pragma the AL files set
        // (Program.cs:1454-1462); we use the static 1..25 set v2 was already shipping —
        // sufficient for the tests/ corpus.
        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
                .Concat(_extraPreprocessorSymbols ?? [])
                .Concat(manifestInputs.PreprocessorSymbols),
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

        // CompilationOptions: v1 shape (Program.cs:1548-1555) plus the manifest-derived
        // compilerFeatures (#1941) and contextSensitiveHelpUrl (#1940) — see
        // ReadManifestCompilerInputs. Both used to be left at their "" / None defaults on
        // this top-level path regardless of what the app's own app.json declared.
        var compOpts = new NavCA.CompilationOptions(
            continueBuildOnError: true,
            target: NavCA.CompilationTarget.OnPrem,
            generateOptions:
                NavCA.CompilationGenerationOptions.Code |
                NavCA.CompilationGenerationOptions.Navigation,
            compilerFeatures: manifestInputs.CompilerFeatures,
            contextSensitiveHelpUrl: manifestInputs.ContextSensitiveHelpUrl);

        // Identity: use the bundle's REAL app.json identity when set, else a synthetic
        // one. The real identity matters when a dependency grants this app access via
        // internalsVisibleTo — BC matches the grant against the consuming compilation's
        // appId/publisher, so a synthetic "AlRunner"/deterministic-guid identity would
        // fail to match and produce AL0161 on the dep's Access=Internal members.
        var appId = _currentAppId ?? DeterministicGuid(moduleName);
        var compilation = NavCA.Compilation.Create(
            moduleName: moduleName,
            publisher: _currentPublisher ?? "AlRunner",
            version: _currentVersion ?? new Version(1, 0, 0, 0),
            appId: appId,
            syntaxTrees: trees,
            options: compOpts);

        // #1899: give the compiler a file-access abstraction anchored at the APP ROOT
        // (where app.json lives), not `dirs` (the src/ subdirectory this method receives
        // as alFolders). Without an IFileSystem, BC's compiler cannot resolve ANY
        // ControlAddIn resource path (Scripts/StartupScript/StyleSheets/Images) and raises
        // AL0327 "Missing file" for every declaration, even when the file exists exactly
        // where declared. RelativeFileSystem is a public BC API — no new dependency.
        //
        // #2151: a report's LayoutFile can ALSO be declared file-relative ("./Foo.rdlc"),
        // which real BC resolves against the declaring .al file's own directory, not the app
        // root — see ReportLayoutFileSystem's header for the full investigation. Compute the
        // file system ONCE here so every retry compile below (--tdd generation, EMIT-EXCLUDED
        // retry) reuses the identical override table instead of re-scanning alFiles each time.
        var compileFileSystem = ReportLayoutFileSystem.Build(alFiles, appRootDir);
        if (compileFileSystem != null)
            compilation = compilation.WithFileSystem(compileFileSystem);

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

        // Opt-in diagnostic (AL_RUNNER_DIAG_EMITRETRY=1) for investigating a module that still
        // fails to emit after the retry loop below: prints the ORIGINAL (pre-retry) declaration
        // diagnostics, including AL0275/AL0264 ambiguous/duplicate-reference counts, which are a
        // distinct class of failure from the emitter-crash / plain-emit-diagnostic cases the
        // retry loop actually excludes-and-retries on. Silent by default — does not affect the
        // fix's behaviour, only visibility when diagnosing a NEW module that still fails.
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_EMITRETRY") == "1")
        {
            var origDecl = compilation.GetDeclarationDiagnostics()
                .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error).ToList();
            var origAmbig = origDecl.Where(d => d.Id == "AL0275" || d.Id == "AL0264").ToList();
            Console.Error.WriteLine($"[DIAG-RETRY] {moduleName} ORIGINAL (pre-retry) declDiags={origDecl.Count} ambiguous(AL0275/AL0264)={origAmbig.Count} caught={caught?.GetType().Name ?? "<none>"} captured={outputter.Captured.Count} specsLen={specs.Length}");
            foreach (var d in origDecl.Take(15))
                Console.Error.WriteLine($"[DIAG-RETRY]   [{d.Id}] @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
            if (origAmbig.Count > 0)
            {
                Console.Error.WriteLine($"[DIAG-RETRY]   _currentAppId={_currentAppId}");
                foreach (var s in specs)
                    Console.Error.WriteLine($"[DIAG-RETRY]   spec: {s.Publisher}/{s.Name}/{s.Version} appId={s.AppId}");
            }
            foreach (var d in origAmbig.Take(5))
                Console.Error.WriteLine($"[DIAG-RETRY]   {d.Id} @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
        }

        // Resilience: Compilation.Emit is atomic PER MODULE — one broken, unrelated object
        // (e.g. a mock Page crashing BC's emitter with "Unexpected value 'BadExpression'", or a
        // mock Codeunit method with "Unexpected value 'None' of type NavTypeKind") throws an
        // AggregateException that zeroes out EVERY object in the module, including otherwise-
        // clean codeunits elsewhere in a large (100+ object) test-library package. That silent
        // total loss is what produces the cryptic "NavNCLMissingMethodException ... object with
        // ID 0" at runtime for a dependency compile: the still-good codeunit's type is never
        // resolvable, so codeunit dispatch falls back to a NoOp stub whose inherited (un-
        // overridden) OnInvoke throws for ANY procedure call — regardless of the NoOp's own
        // (correctly-stamped) ObjectId. See DependencyLoader's Tier-3 compile / CodeunitPatches'
        // NavCodeunitHandle_CreateTarget fallback.
        //
        // BC's own AggregateException names each failing object individually
        // ("Object:'<Type> <Namespace>.\"<Name>\"' ..."). Parse those names, drop ONLY the
        // source files that declare them, and retry. Excluding one broken object can surface
        // ANOTHER previously-masked one (e.g. an ambiguous-reference bind error that only
        // manifests once its sibling stops crashing emit first), so retry iteratively — each
        // round drops whatever NEW objects the latest exception names — bounded so a
        // pathological module can't loop forever. If the module still produces 0 sources after
        // exhausting rounds, the ORIGINAL failure is preserved for the EMIT-ZERO diagnostic
        // below (no regression versus the pre-fix behaviour).
        //
        // This comment used to claim "Never silent: every excluded object ... is always
        // logged". That was wrong, and the wrongness cost real coverage. The lines below DO
        // reach Console.Error, but they carry a `[BcCompiler]` prefix and Log's FilteredWriter
        // drops every `[Component]`-tagged line unless --verbose — so at default verbosity an
        // exclusion produced NO output at all, the vanished objects' tests simply never
        // appeared in the total, and the run still exited 0. That is how a stale System.app
        // quietly cost the al-language corpus 7 tests. Logging is therefore NOT the mechanism
        // that makes this loud: `excludedObjects` is returned to the caller, which fails the
        // bundle. Keep both — the log explains, the return value enforces.
        // --tdd (issue #2001): before falling back to exclude-and-retry, try to infer and
        // generate the missing member(s) any AL0132 ("does not contain a definition for")
        // diagnostic names, directly into the SOURCE-COMPILED implementing app's own
        // SyntaxTree (see TddGeneration.cs's header for the full rationale). Only attempted
        // on a CLEAN diagnostic failure — not an emitter crash — so the crash-handling branch
        // of the retry loop just below is completely unaffected when nothing was generated.
        // A wrong or unrecognized guess costs nothing beyond this attempt: the recompile right
        // here either succeeds outright, or leaves whatever's still broken for the SAME
        // exclude-and-retry loop that runs unconditionally afterwards.
        var tddGeneratedMembers = new List<TddGeneratedMember>();
        if (_tddMode && caught == null && emitResult != null && !emitResult.Success)
        {
            var newlyGenerated = TddGeneration.Generate(compilation, trees, parseOpts, emitResult);
            if (newlyGenerated.Count > 0)
            {
                tddGeneratedMembers.AddRange(newlyGenerated);
                var genCompilation = NavCA.Compilation.Create(
                    moduleName: moduleName, publisher: _currentPublisher ?? "AlRunner",
                    version: _currentVersion ?? new Version(1, 0, 0, 0), appId: appId,
                    syntaxTrees: trees, options: compOpts);
                if (compileFileSystem != null)
                    genCompilation = genCompilation.WithFileSystem(compileFileSystem);
                if (refLoader != null)
                {
                    genCompilation = genCompilation.WithReferenceLoader(refLoader);
                    if (specs.Length > 0) genCompilation = genCompilation.AddReferences(specs);
                }
                genCompilation = genCompilation.WithDotNetResolverFactory(GetOrCreateDotNetFactory());
                var genOutputter = new CaptureOutputter();
                Exception? genCaught = null;
                NavEmit.EmitResult? genEmitResult = null;
                try { genEmitResult = genCompilation.Emit(NavCA.EmitOptions.Default, genOutputter); }
                catch (Exception exGen) { genCaught = exGen; }

                Console.Error.WriteLine(
                    $"[BcCompiler] {moduleName}: --tdd generated {newlyGenerated.Count} missing member(s) and " +
                    $"recompiled ({(genEmitResult?.Success == true ? "compile now succeeds" : "still incomplete, falling through to exclusion")}): " +
                    string.Join(", ", newlyGenerated.Select(g => $"{g.ObjectDisplayName}.{g.Signature}")));

                outputter = genOutputter;
                caught = genCaught;
                emitResult = genEmitResult;
                compilation = genCompilation;
            }
        }

        // Hoisted out of the retry block below so it survives into the returned
        // BcEmitOutput: the caller has to fail the run when anything was excluded, and
        // a stderr line alone does not do that (Log's [Component] filter eats it, and
        // nothing counts it).
        var excludedObjects = new List<string>();
        // --tdd only (issue #1997): file path + the diagnostics that identified each
        // excluded object, captured HERE — inside the round that identifies it — because
        // `emitResult`/`caught` get reassigned to the next (smaller) retry compile's
        // result at the bottom of this loop, at which point the diagnostics that named
        // an EARLIER round's excluded object are no longer reachable from any variable
        // in scope. Left null (not merely empty) when not in --tdd mode, so the emitted
        // BcEmitOutput.TddExcludedDetails is null on the default path exactly as before
        // this issue — no behavioural difference for a non-tdd caller.
        var tddDetails = _tddMode ? new List<TddExcludedObjectDetail>() : null;
        {
            const int maxRounds = 10;
            // Indices are always relative to the ORIGINAL alFiles/trees arrays (captured once,
            // before any exclusion round) — rebuilding `trees` smaller each round while still
            // indexing with original-file indices would silently misalign the two and blow up
            // with an IndexOutOfRangeException on the second round onward.
            var originalTrees = trees;
            var keepIdx = Enumerable.Range(0, alFiles.Count).ToList();
            var allExcluded = excludedObjects;
            int round = 0;
            // A round can fail two different ways and both are handled the same (exclude the
            // culprit file(s), retry):
            //   1. Compilation.Emit THROWS (an emitter crash on a bound tree, e.g. "Unexpected
            //      value 'BadExpression'") — BC's AggregateException names the failing objects
            //      textually ("Object:'<Type> <Ns>."<Name>"'"), matched back to a source file
            //      via DeclaresObject.
            //   2. Excluding round 1's crash-causing object(s) can itself surface a NEW, plain
            //      compile error with NO crash (Emit returns Success=false, 0 sources, nothing
            //      thrown) — e.g. another object that referenced the just-excluded type now
            //      fails to resolve it. Those diagnostics carry a real Location.SourceTree, so
            //      the offending file is identified directly (no text matching needed).
            while (outputter.Captured.Count == 0 && round < maxRounds)
            {
                List<int> nextKeepIdx;
                List<string> roundExcluded;
                if (caught != null)
                {
                    var failing = ExtractFailingObjectRefs(caught.Message);
                    if (failing.Count == 0) break;
                    nextKeepIdx = new List<int>();
                    roundExcluded = new List<string>();
                    foreach (var i in keepIdx)
                    {
                        string src;
                        try { src = File.ReadAllText(alFiles[i]); }
                        catch { nextKeepIdx.Add(i); continue; }
                        var hit = failing.FirstOrDefault(f => DeclaresObject(src, f.Type, f.Namespace, f.Name));
                        if (hit.Name != null)
                        {
                            var label = $"{hit.Type} {hit.Namespace}.\"{hit.Name}\"";
                            roundExcluded.Add(label);
                            // No structured Location for an emitter-crash exclusion — only the
                            // exception's own message names it. Still useful for a --tdd
                            // synthetic failure: it says WHICH object and WHY, even without a
                            // path@line:col anchor.
                            tddDetails?.Add(new TddExcludedObjectDetail(
                                alFiles[i], label,
                                new[] { $"emit-crash: {label} — {caught.Message.Split('\n', 2)[0]}" }));
                        }
                        else
                            nextKeepIdx.Add(i);
                    }
                }
                else if (emitResult != null && !emitResult.Success)
                {
                    if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_EMITRETRY") == "1")
                    {
                        var errs = emitResult.Diagnostics.Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error).ToList();
                        Console.Error.WriteLine($"[DIAG-RETRY] {moduleName} round{round + 1}-diag: {errs.Count} error diagnostic(s)");
                        foreach (var d in errs.Take(15))
                            Console.Error.WriteLine($"[DIAG-RETRY]   [{d.Id}] @ {d.Location}: {d.GetMessage().Split('\n', 2)[0]}");
                    }
                    var badTrees = new HashSet<Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.SyntaxTree>(
                        emitResult.Diagnostics
                            .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error && d.Location.IsInSource)
                            .Select(d => d.Location.SourceTree!));
                    if (badTrees.Count == 0) break;
                    nextKeepIdx = new List<int>();
                    roundExcluded = new List<string>();
                    foreach (var i in keepIdx)
                    {
                        if (badTrees.Contains(originalTrees[i]))
                        {
                            var label = Path.GetFileNameWithoutExtension(alFiles[i]);
                            roundExcluded.Add(label);
                            if (tddDetails != null)
                            {
                                var objDiags = emitResult.Diagnostics
                                    .Where(d => d.Severity == NavDiag.DiagnosticSeverity.Error
                                        && d.Location.IsInSource
                                        && d.Location.SourceTree == originalTrees[i])
                                    .Select(d => $"{d.Location}: error {d.Id}: {d.GetMessage().Split('\n', 2)[0]}")
                                    .ToList();
                                tddDetails.Add(new TddExcludedObjectDetail(alFiles[i], label, objDiags));
                            }
                        }
                        else
                            nextKeepIdx.Add(i);
                    }
                }
                else break; // no exception and no failed EmitResult to learn from — nothing to exclude

                if (roundExcluded.Count == 0 || nextKeepIdx.Count == 0) break; // nothing new identified, or nothing left

                round++;
                allExcluded.AddRange(roundExcluded);
                Console.Error.WriteLine(
                    $"[BcCompiler] {moduleName}: EMIT-FAIL round {round} on {roundExcluded.Count} broken object(s) " +
                    $"unrelated to the rest of the module — excluding and retrying: {string.Join(", ", roundExcluded)}");
                keepIdx = nextKeepIdx;

                var retryTrees = keepIdx.Select(i => originalTrees[i]).ToArray();
                var retryCompilation = NavCA.Compilation.Create(
                    moduleName: moduleName,
                    publisher: _currentPublisher ?? "AlRunner",
                    version: _currentVersion ?? new Version(1, 0, 0, 0),
                    appId: appId,
                    syntaxTrees: retryTrees,
                    options: compOpts);
                // #1899: same file system as the primary compile above — without it, a
                // retry after excluding an unrelated broken object would still raise AL0327
                // for a perfectly-resolvable ControlAddIn resource and could exclude it too.
                if (compileFileSystem != null)
                    retryCompilation = retryCompilation.WithFileSystem(compileFileSystem);
                if (refLoader != null)
                {
                    retryCompilation = retryCompilation.WithReferenceLoader(refLoader);
                    if (specs.Length > 0)
                        retryCompilation = retryCompilation.AddReferences(specs);
                }
                retryCompilation = retryCompilation.WithDotNetResolverFactory(GetOrCreateDotNetFactory());
                var retryOutputter = new CaptureOutputter();
                Exception? retryCaught = null;
                Microsoft.Dynamics.Nav.CodeAnalysis.Emit.EmitResult? retryEmitResult = null;
                try { retryEmitResult = retryCompilation.Emit(NavCA.EmitOptions.Default, retryOutputter); }
                catch (Exception ex2) { retryCaught = ex2; }

                outputter = retryOutputter;
                caught = retryCaught;
                emitResult = retryEmitResult;
                compilation = retryCompilation;
                trees = retryTrees;
            }
            if (allExcluded.Count > 0)
            {
                if (outputter.Captured.Count > 0)
                    Console.Error.WriteLine(
                        $"[BcCompiler] {moduleName}: retry after excluding {allExcluded.Count} broken object(s) " +
                        $"across {round} round(s) succeeded — {outputter.Captured.Count} object(s) compiled");
                else
                    Console.Error.WriteLine(
                        $"[BcCompiler] {moduleName}: retry after excluding {allExcluded.Count} broken object(s) " +
                        $"across {round} round(s) STILL produced 0 sources — giving up (original failure preserved)");
            }
        }


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

        // ── Bundle query-symbol registration ────────────────────────────────────
        // Source-compiled queries (no prebuilt .app in the bundle root) have no
        // SymbolReference.json, so the NCLMetaQuery builder cannot read this bundle's
        // BC-compiler-assigned query column ids — and the emitted Query DLL calls
        // GetColumnByNo with exactly those ids. Without them a query NREs in
        // FindDataImplAsync (NCLMetaQuery==null). The prebuilt-.app path (corpus)
        // already supplies them; this fills the gap for source-only bundles.
        //
        // We extract the queries from the SAME compilation we just emitted (so the ids
        // match the DLL byte-for-byte — no extra compile), serialize them to a temp
        // SymbolReference.json, and register that file so TryGetQuerySymbol finds them.
        // Gated on the bundle actually declaring a query, so the common (no-query)
        // bundle pays nothing.
        //
        // The gate is "the emit produced objects", NOT emitResult.Success. Success is
        // false if ANY object raised an emit-level error, and those errors are routinely
        // unrelated to queries — the al-language corpus emits all 355 of its objects and
        // still reports Success=false purely because 7 reports fail AL1081 report-layout
        // update. Gating on Success there cost the corpus its query column ids (every
        // multi-dataitem query NREd in NavQuery.ValidateTablesNotVirtual with a null
        // NCLMetaQuery) AND made its cache entry permanently incomplete, forcing a full
        // recompile on every single run. The symbols come from the compilation's semantic
        // model, which is intact whether or not a report layout updated.
        LastBundleQuerySymbolsPath = null;
        if (caught == null && outputter.Captured.Count > 0 && BundleDeclaresQuery(alFiles))
        {
            try { EmitAndRegisterBundleQuerySymbols(compilation, moduleName); }
            catch (Exception ex)
            {
                // Never fail the run for this — a query that then can't build its
                // metaquery surfaces its own loud error downstream. But say so
                // unconditionally: silently skipping this costs the bundle its query
                // metadata AND permanently defeats its AL-output cache entry.
                Console.Error.WriteLine(
                    $"[BcCompiler] {moduleName}: bundle query-symbol emit failed — queries in this " +
                    $"bundle will have no NCLMetaQuery and its cache entry stays incomplete: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        var emitOutput = new BcEmitOutput(
            outputter.Captured, alDiags, excludedObjects, tddDetails,
            _tddMode ? tddGeneratedMembers : null);

        // #1902: only a CLEAN success (nothing excluded, every source captured) is trustworthy
        // as a RAD baseline — a module that only compiled after dropping broken objects must
        // not let a later incremental cycle silently treat those objects as "still there".
        if (trackIncrementalBaseline && caught == null && excludedObjects.Count == 0 && outputter.Captured.Count > 0)
        {
            try
            {
                RecordIncrementalBaseline(
                    moduleName, compilation, alFiles, outputter.Captured, specs,
                    manifestInputs, manifestAppJsonPath, appId, _currentPublisher ?? "AlRunner", _currentVersion ?? new Version(1, 0, 0, 0),
                    appRootDir, emitOutput);
            }
            catch (Exception ex)
            {
                // Never fail the run for this — losing the baseline just means the NEXT --watch
                // cycle falls back to a full rebuild instead of going incremental. Say so; a
                // developer staring at an unexpectedly slow warm cycle needs the reason.
                Console.Error.WriteLine(
                    $"[BcCompiler] {moduleName}: failed to record an incremental (RAD) baseline — " +
                    $"the next --watch cycle will do a full rebuild: {ex.GetType().Name}: {ex.Message}");
                _radBaselines.Remove(moduleName);
            }
        }

        return emitOutput;
    }

    // Cheap text probe: does any source file declare an AL `query` object? Avoids
    // building the (non-trivial) ModuleDefinition for the 99% of bundles with none.
    /// <summary>
    /// Path of the SymbolReference.json written by the most recent emit whose bundle
    /// declared a query, or null. The caller copies it next to the AL-output cache DLL
    /// so a later cache HIT — which skips Emit entirely — can replay the query symbols.
    /// </summary>
    public static string? LastBundleQuerySymbolsPath { get; private set; }

    /// <summary>
    /// True when any of the given paths declares a `query &lt;id&gt;` object. Accepts both
    /// .al FILES (the emit path passes those) and DIRECTORIES (the cache path passes
    /// suite roots, which are recursed for *.al). Anything unreadable is reported
    /// loudly rather than swallowed: a false negative here silently costs a bundle its
    /// query metadata on every cache HIT.
    /// </summary>
    public static bool BundleDeclaresQuery(IEnumerable<string> paths)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"(^|\n)\s*query\s+\d+\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*.al", SearchOption.AllDirectories))
                        if (rx.IsMatch(File.ReadAllText(f))) return true;
                }
                else if (File.Exists(p) && rx.IsMatch(File.ReadAllText(p)))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BcCompiler] query-declaration probe failed for {p}: {ex.Message}");
            }
        }
        return false;
    }

    // BC's Compilation.Emit AggregateException names each failing object individually, e.g.:
    //   "Failure while emitting object. Object:'Page System.TestLibraries.Reflection.\"Record
    //    Selection Test Page\"' (Unexpected value 'BadExpression' ...)"
    //   "Failure while emitting method. Object:'Codeunit System.TestLibraries.Email.\"Connector
    //    Mock\"' Method:'Initialize()' (...)"
    //   "Failure while emitting metadata for object:'Table ...\"Cues And KPIs Test 1 Cue\"' (...)"
    // But an object declared with NO `namespace` line at all (still legal AL — namespaces are
    // an opt-in convention, not mandatory) is named WITHOUT the "<Namespace>." segment, e.g.:
    //   "Failure while emitting method. Object:'Codeunit \"EmitRetryTest Bad\"' Method:'Crash()' (...)"
    // The namespace group must therefore be OPTIONAL, not required, or every namespace-less
    // object silently fails to match and the retry loop can't identify it (confirmed via a
    // synthetic repro in AlRunner.Tests/BcCompilerEmitRetryTests.cs — the very first version of
    // this regex required a namespace and produced zero matches for a namespace-less crash).
    private static readonly System.Text.RegularExpressions.Regex _failingObjectRx = new(
        @"[Oo]bject:'(\w+) (?:([\w.]+)\.)?""([^""]+)""'",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<(string Type, string Namespace, string Name)> ExtractFailingObjectRefs(string message)
    {
        var result = new List<(string, string, string)>();
        foreach (System.Text.RegularExpressions.Match mm in _failingObjectRx.Matches(message))
            result.Add((mm.Groups[1].Value, mm.Groups[2].Value, mm.Groups[3].Value));
        return result;
    }

    // True when an AL source file has an object header line for the given (type, name) — e.g.
    // `codeunit 134688 "Connector Mock"` — AND, when a namespace was named, also declares that
    // namespace (e.g. `namespace System.TestLibraries.Email;`). An empty namespace means BC's
    // failure message named the object with none (see _failingObjectRx above), so the namespace
    // check is skipped rather than requiring a `namespace ...;` line that may not exist.
    // Used to identify exactly which source file(s) to drop from a retry-without-the-broken-
    // object compile.
    private static bool DeclaresObject(string src, string type, string ns, string name)
    {
        var headerRx = new System.Text.RegularExpressions.Regex(
            $@"(?im)^\s*{System.Text.RegularExpressions.Regex.Escape(type)}\s+\d+\s+""{System.Text.RegularExpressions.Regex.Escape(name)}""");
        if (!headerRx.IsMatch(src)) return false;
        if (string.IsNullOrEmpty(ns)) return true;
        var nsRx = new System.Text.RegularExpressions.Regex(
            $@"(?im)^\s*namespace\s+{System.Text.RegularExpressions.Regex.Escape(ns)}\s*;");
        return nsRx.IsMatch(src);
    }

    // Serialize the compilation's SymbolReference (which carries Queries[] with the
    // BC-compiler-assigned column ids) to a temp file and register it for query-symbol
    // lookup. One file per (moduleName) — overwritten each run so it tracks the source.
    private static void EmitAndRegisterBundleQuerySymbols(NavCA.Compilation compilation, string moduleName)
    {
        var safe = new string(moduleName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-query-symbols");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, safe + ".SymbolReference.json");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            SymbolJsonWriter.WriteSymbolJson(compilation, fs);
        AlRunner.Patches.RecordPatches.RegisterBundleQuerySymbolsJson(path);
        LastBundleQuerySymbolsPath = path;
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
    /// <param name="appRootDir">
    /// The directory containing this dep's own app.json — see the identically-named
    /// parameter on <see cref="Emit"/> for why (#1899). When omitted, falls back to
    /// whichever of <paramref name="alFolders"/> already carries an app.json — the same
    /// directory <c>ivtRefs</c> below is read from — since every current caller of this
    /// overload passes a single flat directory that already IS the app root.
    /// </param>
    public void EmitDepSymbols(
        IEnumerable<string> alFolders, string moduleName,
        Guid appId, string publisher, Version version, string symbolsJsonPath,
        string? appRootDir = null)
    {
        var dirs = alFolders.Where(Directory.Exists).Distinct().ToList();
        var alFiles = dirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.al", SearchOption.AllDirectories))
            .Distinct().ToList();
        if (alFiles.Count == 0)
            throw new InvalidOperationException(
                $"BcCompiler.EmitDepSymbols: no .al files under {string.Join(", ", dirs)}");

        // Locate the dep's own app.json BEFORE building ParseOptions/CompilationOptions —
        // internalsVisibleTo, and the manifest-derived compiler inputs read below
        // (preprocessorSymbols/features/contextSensitiveHelpUrl — #1898/#1940/#1941/#1943),
        // all need it in hand for the ctors themselves.
        var foundAppJson = dirs.Select(d => Path.Combine(d, "app.json")).FirstOrDefault(File.Exists);
        var manifestInputs = ReadManifestCompilerInputs(foundAppJson);

        // Preprocessor symbols: same union as Emit() — CLEANSCHEMA1..25, any caller-supplied
        // (--define) symbols, AND this dep's OWN manifest symbols (#1943) — never the
        // consuming bundle's, since foundAppJson is this dep's own app.json.
        var parseOpts = new NavCA.ParseOptions(
            runtimeVersion: null!,
            preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}")
                .Concat(_extraPreprocessorSymbols ?? [])
                .Concat(manifestInputs.PreprocessorSymbols),
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
                NavCA.CompilationGenerationOptions.Code | NavCA.CompilationGenerationOptions.Navigation,
            // #1941: same manifest → CompilerFeatures mapping as Emit() — a dep declaring
            // NoImplicitWith in its own app.json must compile under that feature too, not
            // just when it happens to be the top-level bundle.
            compilerFeatures: manifestInputs.CompilerFeatures,
            // #1898: BC's AL0543 check ("The manifest property 'contextSensitiveHelpUrl'
            // must be set in order to use the property 'ContextSensitiveHelpPage'") reads
            // THIS CompilationOptions field directly — there is no separate "give the
            // compiler the manifest" API on Compilation/Compilation.Create (confirmed via
            // reflection over Compilation's public surface: no WithManifest, and
            // CompilationOptions' ctor takes contextSensitiveHelpUrl as a plain string
            // param, default ""). Leaving it unset here — as EmitDepSymbols always did
            // before this fix — makes BC treat the property as unset even when the dep's
            // own app.json genuinely sets it, so a dependency using
            // ContextSensitiveHelpPage always failed AL0543 regardless of its manifest.
            // Reading the real value here restores parity with what alc.exe does (and
            // with what the primary bundle-compile path already tolerates via its own,
            // separate leniency — see the Emit() docs above). A dep whose manifest
            // genuinely omits the URL still gets "" here and AL0543 still fires,
            // preserving the diagnostic for an actually-invalid manifest.
            contextSensitiveHelpUrl: manifestInputs.ContextSensitiveHelpUrl);
        // Propagate the dep's own `internalsVisibleTo` (from its app.json) into the
        // Compilation. BC populates IModuleSymbol.InternalsVisibleToModules ONLY from
        // this dedicated Create parameter — not from the manifest — so without it a
        // dependent app hits AL0161 on the dep's Access=Internal members even when the
        // grant exists. (main:Program.cs BuildInternalsVisibleToRefs.)
        var ivtRefs = ReadInternalsVisibleToRefs(foundAppJson);

        var compilation = NavCA.Compilation.Create(
            moduleName: moduleName, publisher: publisher, version: version,
            appId: appId, internalsVisibleTo: ivtRefs, syntaxTrees: trees, options: compOpts);

        // #1899: same rationale as Emit's appRootDir — without an IFileSystem, a
        // ControlAddIn inside a source dependency raises AL0327 for every resource path.
        // Falls back to the directory the manifest was already found in (foundAppJson)
        // when the caller didn't pass one explicitly.
        // #2151: same file-relative LayoutFile override as Emit's compileFileSystem — a
        // source-dependency report can live in a subdirectory too.
        var effectiveAppRoot = appRootDir ?? (foundAppJson != null ? Path.GetDirectoryName(foundAppJson) : null);
        var depCompileFileSystem = ReportLayoutFileSystem.Build(alFiles, effectiveAppRoot);
        if (depCompileFileSystem != null)
            compilation = compilation.WithFileSystem(depCompileFileSystem);

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
        // AL0327 = a ControlAddIn resource file (Scripts/StartupScript/StyleSheets/Images)
        // could not be located. These are browser-side assets the headless runner never
        // renders, and they do NOT affect the AL symbol table a dependent app compiles
        // against (the add-in's AL-visible surface — procedures/events — is fully declared
        // in the .al). The primary bundle compile (see Compilation.Emit path above) never
        // checks declaration diagnostics and so already tolerates AL0327; mirror that here
        // rather than failing a source-dep whose only fault is a missing JS/CSS resource.
        // AL1023 = "the package file X is not valid": a .app in a scanned package dir that
        // carries no SymbolReference.json. It is a complaint about ANOTHER app's package,
        // not about the AL being compiled here, and it says nothing about this module's
        // symbol table. It surfaces when a bundle holding many sibling apps puts one
        // suite's fixture .app into the shared scan set — the primary Compilation.Emit
        // path never inspects declaration diagnostics and so has always tolerated it, and
        // failing here instead means the sibling that actually needs these symbols gets
        // none. If the invalid package were genuinely supplying a type this module needs,
        // the missing type still fails LOUDLY as AL0185 below.
        var errors = compilation.GetDeclarationDiagnostics()
            .Where(d => d.Severity == NavCA.Diagnostics.DiagnosticSeverity.Error)
            .Where(d => d.Id != "AL0327" && d.Id != "AL1023")
            .ToList();
        if (errors.Count > 0)
        {
            var detail = string.Join("; ", errors.Take(10).Select(d => $"{d.Id} {d.GetMessage()}"));
            // #2178: an AL1022 here names the dirs BC searched, which after deduplication is
            // one opaque content-addressed staging path. Say what it stands for, and which
            // packages were left out of it, rather than leaving the reader to guess whether
            // their --package-cache reached the compile at all.
            detail += DescribeStagedSearchSet(detail) ?? string.Empty;
            throw new InvalidOperationException(
                $"source dependency '{moduleName}' does not compile ({errors.Count} error(s)): {detail}");
        }

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
    /// The subset of an app.json's properties that feed BC's <see cref="NavCA.ParseOptions"/>
    /// / <see cref="NavCA.CompilationOptions"/>, read in one pass and shared by both
    /// <see cref="Emit"/> (top-level bundle compile) and <see cref="EmitDepSymbols"/> (source
    /// dependency compile). See #1940/#1941/#1943: before this, each compile path built its
    /// own ParseOptions/CompilationOptions by hand and each of these three manifest properties
    /// independently went missing on at least one of the two paths — #1898 fixed
    /// <c>contextSensitiveHelpUrl</c> only on <see cref="EmitDepSymbols"/>, and neither path
    /// ever read <c>features</c> or <c>preprocessorSymbols</c> at all.
    /// </summary>
    internal readonly record struct ManifestCompilerInputs(
        IReadOnlyList<string> PreprocessorSymbols,
        NavCA.CompilerFeatures CompilerFeatures,
        string ContextSensitiveHelpUrl)
    {
        public static readonly ManifestCompilerInputs Empty =
            new(Array.Empty<string>(), NavCA.CompilerFeatures.None, "");

        /// <summary>
        /// Deterministic fragment that changes iff any manifest property this struct reads
        /// changes — folded into the AL-output cache key (see <c>ComputeAlCacheKey</c> in
        /// Program.cs) so editing app.json's <c>preprocessorSymbols</c>/<c>features</c>/
        /// <c>contextSensitiveHelpUrl</c> invalidates a warm cache entry instead of silently
        /// serving a DLL compiled under the OLD values (#1943's cache-key requirement).
        /// </summary>
        public string CacheKeyFragment =>
            string.Join(",", PreprocessorSymbols.OrderBy(s => s, StringComparer.Ordinal)) +
            "|" + (int)CompilerFeatures + "|" + ContextSensitiveHelpUrl;
    }

    /// <summary>
    /// Read <c>preprocessorSymbols</c>, <c>features</c>, and <c>contextSensitiveHelpUrl</c>
    /// from an app.json in one pass. Every field defaults to BC's own "unset" default (empty
    /// list / <see cref="NavCA.CompilerFeatures.None"/> / <c>""</c>) when the manifest is
    /// missing, unreadable, or genuinely omits the property — so a genuinely invalid/absent
    /// manifest still produces the diagnostics real BC would (AL0543 for a missing
    /// contextSensitiveHelpUrl, the wrong <c>#if</c> branch for an undeclared symbol, implicit
    /// <c>with</c> for an app that never opted into NoImplicitWith).
    /// </summary>
    internal static ManifestCompilerInputs ReadManifestCompilerInputs(string? appJsonPath)
    {
        if (appJsonPath == null || !File.Exists(appJsonPath)) return ManifestCompilerInputs.Empty;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appJsonPath));
            var root = json.RootElement;

            var symbols = new List<string>();
            if (root.TryGetProperty("preprocessorSymbols", out var symEl)
                && symEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in symEl.EnumerateArray())
                    if (e.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.IsNullOrEmpty(e.GetString()))
                        symbols.Add(e.GetString()!);

            // Manifest `features` strings are mapped onto NavCA.CompilerFeatures by NAME
            // (case-insensitive) — e.g. "NoImplicitWith" matches CompilerFeatures.NoImplicitWith
            // exactly. The app.json schema also allows feature strings that are NOT compiler
            // switches at all (e.g. "TranslationFile", "GenerateCaptions" — those drive
            // packaging/translation-file generation elsewhere, not parsing/binding).
            // Enum.TryParse fails for those and they are silently skipped, matching alc's own
            // tolerance for a manifest declaring features this compile path doesn't act on —
            // never a hard failure for an unrecognised-but-legal feature string.
            var features = NavCA.CompilerFeatures.None;
            if (root.TryGetProperty("features", out var featEl)
                && featEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in featEl.EnumerateArray())
                {
                    if (e.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    var name = e.GetString();
                    if (!string.IsNullOrEmpty(name)
                        && Enum.TryParse<NavCA.CompilerFeatures>(name, ignoreCase: true, out var f))
                        features |= f;
                }

            var helpUrl = root.TryGetProperty("contextSensitiveHelpUrl", out var helpEl)
                && helpEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? helpEl.GetString() ?? ""
                : "";

            return new ManifestCompilerInputs(symbols, features, helpUrl);
        }
        catch { return ManifestCompilerInputs.Empty; }
    }

    /// <summary>
    /// <see cref="ManifestCompilerInputs.CacheKeyFragment"/> for the app.json under
    /// <paramref name="appRootDir"/>, for Program.cs's <c>ComputeAlCacheKey</c> — the only
    /// caller outside this class that needs the manifest's compiler-input fingerprint without
    /// needing the full <see cref="ManifestCompilerInputs"/> shape.
    /// </summary>
    public static string ReadManifestCacheKeyFragment(string? appRootDir)
    {
        var appJsonPath = appRootDir != null ? Path.Combine(appRootDir, "app.json") : null;
        return ReadManifestCompilerInputs(appJsonPath).CacheKeyFragment;
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
        // Cross-platform home (POSIX HOME is null on Windows — see AlRunnerPaths).
        var home = AlRunner.Infrastructure.AlRunnerPaths.UserHome;
        if (string.IsNullOrEmpty(home)) yield break;

        // Match the process-global selected BC version (BcArtifacts.SelectedVersion) so
        // compile symbols, runtime deps, and the engine all agree. These caches may carry
        // a different patch level than the artifacts tree, so match on major.minor.
        var sel = AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
        var mmPrefix = $"{sel.Major}.{sel.Minor}";

        foreach (var rel in new[] { ".local/share/al-runner/symbols", ".bcartifacts.cache/sandbox" })
        {
            var root = Path.Combine(home, rel);
            if (!Directory.Exists(root)) continue;
            string bestVer;
            try
            {
                bestVer = AlRunner.Infrastructure.BcArtifacts.SelectArtifactVersionDir(root, mmPrefix);
            }
            catch (InvalidOperationException)
            {
                continue; // optional cache without a matching version
            }

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

            // Capture (id, name, options[], indexes[], captions[]) for AL enum types so
            // the runtime NCLEnumMetadata.Create(int) hook can return real
            // GetNames()/GetOrdinals()/GetCaptionFromIndex() data instead of
            // NCLOptionMetadata.Default (which throws
            // NavNCLNotSupportedOperationException) or forwarding captions to the
            // member name (issue #1775 — Format(<enum value>) returned the AL
            // identifier instead of the declared Caption). Enum extensions also flow
            // through here as IEnumExtensionTypeSymbol; both expose Values via the
            // IEnumBaseTypeSymbol interface. Per BC's own
            // SourceEnumExtensionTypeSymbol.LazyGetEnumValues, an extension's Values
            // NEVER includes the base enum's values — only its own declared ones — so
            // an extension must be registered against the base enum's Id (via its
            // Target), not merged as if it were the base (issue #1625: registering
            // both under the same dictionary slot made whichever AddApplicationObject
            // call fired last silently clobber the other instead of merging).
            if (symbol is NavCA.IEnumBaseTypeSymbol enumSym)
            {
                var values = enumSym.Values;
                var options = new string[values.Length];
                var indexes = new int[values.Length];
                var implementations = new int[values.Length][];
                var captions = new string?[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    options[i] = values[i].Name ?? string.Empty;
                    indexes[i] = values[i].Ordinal;
                    implementations[i] = ReadEnumValueImplementations(values[i]);
                    captions[i] = ReadEnumValueCaption(values[i]);
                }
                if (symbol is NavCA.IEnumExtensionTypeSymbol enumExtSym
                    && enumExtSym.Target is NavCA.ISymbolWithId targetSym)
                {
                    AlEnumMetadataRegistry.RegisterExtension(targetSym.Id, enumSym.Name, options, indexes, implementations, captions);
                }
                else
                {
                    AlEnumMetadataRegistry.Register(enumSym.Id, enumSym.Name, options, indexes, implementations, captions);
                }
            }
            // Capture the per-report runtime metadata XML the emit pipeline hands us
            // (the same XML the service tier stores at publish time). Consumed at run
            // time by NavReportSync.StubInitializeMetadata to build a REAL MetaReport
            // so BC's report execution chain runs on genuine metadata.
            if (symbol is NavCA.IReportTypeSymbol reportSym && !string.IsNullOrEmpty(metadata))
            {
                AlReportMetadataRegistry.Register(reportSym.Id, metadata);
                // The emitted metadata XML's <Layouts> block carries only
                // Name/Caption/Summary — the layout's Type, MimeType and
                // LayoutFile live on the compiler's own ReportLayoutSymbol.
                // Capture those so the "Report Layout List" virtual table
                // (2000000234) can be populated with real per-layout values.
                CaptureReportLayouts(reportSym, metadata);
            }

            // Same capture for pages. NCLMetaForm.LoadMetadata() parses this XML into a
            // real MetaForm with the page's full control tree — without it the runner's
            // NCLMetaForm is a skeleton with no controls, which is why a TestPage control
            // bound to anything but a Rec field has nowhere to resolve to.
            // See AlPageMetadataRegistry.cs (and the cache-HIT sidecar it documents).
            if (symbol is NavCA.IPageTypeSymbol pageSym && !string.IsNullOrEmpty(metadata))
                AlPageMetadataRegistry.Register(pageSym.Id, metadata);

            // Same capture for xmlports. NCLMetaXmlPort.LoadMetadata() parses this XML into
            // a real MetaXmlPort with the port's full node schema; without it BC's own
            // XmlPort engine has nothing to import/export against and both
            // NCLMetaXmlPort.CreateObjectInstance and GetMetadataFromLoader NRE.
            // See AlXmlPortMetadataRegistry.cs (and the cache-HIT sidecar it documents).
            if (symbol is NavCA.IXmlPortTypeSymbol xmlPortSym && !string.IsNullOrEmpty(metadata))
                AlXmlPortMetadataRegistry.Register(xmlPortSym.Id, metadata);

            if (Environment.GetEnvironmentVariable("BCCOMPILER_TRACE") == "1")
                Console.Error.WriteLine($"  emit[{AddCalls}]: {symbol.Name} kind={symbol.GetType().Name} metaLen={metadata?.Length ?? -1}");
            if (Environment.GetEnvironmentVariable("BCCOMPILER_DUMP_CS") == "1")
            {
                var dir = Path.Combine(Path.GetTempPath(), "bccompiler-dump");
                Directory.CreateDirectory(dir);
                var fname = string.Concat(symbol.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".cs";
                File.WriteAllText(Path.Combine(dir, fname), src);
            }
        }

        /// <summary>
        /// Capture the report's <c>rendering { layout(Name) { … } }</c> declarations.
        ///
        /// The AL compiler models each one as a <c>ReportLayoutSymbol</c> member of the
        /// report symbol, carrying <c>LayoutType</c> (RDLC/Word/Excel/Custom),
        /// <c>MimeType</c> and <c>LayoutFile</c>. None of those three survive into the
        /// emitted runtime metadata XML (whose &lt;Layouts&gt; block has only
        /// Name/Caption/Summary), so they are read off the symbol here — the only place
        /// they are available — and merged with the Caption/Summary the XML does carry.
        ///
        /// <c>ReportLayoutSymbol</c> lives in the compiler's internal Symbols namespace
        /// (the public <c>IReportLayoutSymbol</c> exposes nothing), hence reflection.
        /// Every step is defensive: a report whose layouts cannot be read registers no
        /// layouts and behaves exactly as it did before this capture existed.
        /// </summary>
        private static void CaptureReportLayouts(NavCA.IReportTypeSymbol reportSym, string metadataXml)
        {
            try
            {
                if (reportSym is not NavCA.IContainerSymbol container) return;
                var captions = ParseLayoutCaptionsFromMetadata(metadataXml);
                var defaultLayoutName = ReadDefaultRenderingLayoutName(reportSym);
                foreach (var member in container.GetMembers())
                {
                    var t = member.GetType();
                    if (t.Name != "ReportLayoutSymbol" && t.BaseType?.Name != "ReportLayoutSymbol") continue;

                    string name = member.Name ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;


                    var layoutType = ReadSymbolProp(member, "LayoutType")?.ToString() ?? string.Empty;
                    var mimeType = ReadSymbolProp(member, "MimeType") as string ?? string.Empty;
                    var layoutFile = ReadSymbolProp(member, "LayoutFile") as string ?? string.Empty;
                    var resolved = ResolveLayoutFilePath(member, layoutFile);

                    captions.TryGetValue(name, out var cs);
                    AlReportLayoutRegistry.Register(new AlReportLayoutInfo(
                        ReportId: reportSym.Id,
                        Name: name,
                        LayoutType: layoutType,
                        MimeType: mimeType,
                        LayoutFile: layoutFile,
                        ResolvedPath: resolved,
                        Caption: cs.Caption ?? string.Empty,
                        Summary: cs.Summary ?? string.Empty,
                        IsDefault: IsDefaultLayout(name, defaultLayoutName)));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BcCompiler] report-layout capture failed for report {reportSym.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Which layout the report declares as <c>DefaultRenderingLayout</c>, or "" when it
        /// cannot be read. AL requires the property on every report using the
        /// <c>rendering</c> syntax ("Reports that use the rendering syntax must also define
        /// the DefaultRenderingLayout property"), so this is normally present — it is what a
        /// plain <c>Report.SaveAs</c> with no explicit layout selection renders through.
        ///
        /// It is NOT a CLR property on the report symbol, and <c>ReportLayoutSymbol</c> has
        /// no <c>IsDefaultLayout</c> flag (both verified by dumping the symbol surface).
        /// It lives in the symbol's AL property bag, so the walk is
        /// <c>Properties</c> → the entry named <c>DefaultRenderingLayout</c> →
        /// <c>Property</c> (BoundProperty) → <c>PropertyValue</c>
        /// (BoundMemberReferencePropertyValue) → <c>ValueText</c>, which holds the layout
        /// name as written.
        ///
        /// Absence is not an error: the consumer keeps its no-guessing behaviour and simply
        /// declines to hydrate a multi-layout report, exactly as before this existed.
        /// </summary>
        private static string ReadDefaultRenderingLayoutName(NavCA.IReportTypeSymbol reportSym)
        {
            if (ReadSymbolProp(reportSym, "Properties") is not System.Collections.IEnumerable bag)
                return string.Empty;

            foreach (var entry in bag)
            {
                if (entry == null) continue;
                if (!string.Equals(ReadSymbolProp(entry, "Name") as string,
                        "DefaultRenderingLayout", StringComparison.OrdinalIgnoreCase))
                    continue;

                var bound = ReadSymbolProp(entry, "Property");
                var value = bound == null ? null : ReadSymbolProp(bound, "PropertyValue");
                var text = value == null ? null : ReadSymbolProp(value, "ValueText") as string;
                if (!string.IsNullOrWhiteSpace(text)) return text!;
            }
            return string.Empty;
        }

        /// <summary>
        /// Whether this layout is the one the report names in <c>DefaultRenderingLayout</c>.
        /// </summary>
        private static bool IsDefaultLayout(string layoutName, string defaultLayoutName) =>
            !string.IsNullOrEmpty(defaultLayoutName)
            && string.Equals(defaultLayoutName, layoutName, StringComparison.OrdinalIgnoreCase);

        private static object? ReadSymbolProp(object target, string propertyName)
        {
            var p = target.GetType().GetProperty(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
            return p?.GetValue(target);
        }

        /// <summary>
        /// Turn the layout's <c>LayoutFile</c> into an absolute path. #2151: a value marked
        /// explicitly file-relative (a leading "./" or "../" — see
        /// <see cref="ReportLayoutFileSystem.IsFileRelativeMarker"/>) is tried FIRST against
        /// the DECLARING .al file's own directory, matching what real BC's compiler itself now
        /// resolves against (see ReportLayoutFileSystem's header) — this registry used to
        /// assume every LayoutFile was app-root-relative and silently came back empty for
        /// these same six corpus reports even once the compile-time fix landed, because this
        /// is a SEPARATE hand-rolled resolution that never went through the compile's
        /// IFileSystem. Falls back to the original app-root walk for anything that isn't
        /// file-relative, or where the file-relative candidate doesn't exist. Returns "" when
        /// the file cannot be located either way — the layout is still registered, and the
        /// consumer decides how to fail if content is ever demanded.
        /// </summary>
        private static string ResolveLayoutFilePath(object layoutSymbol, string layoutFile)
        {
            if (string.IsNullOrWhiteSpace(layoutFile)) return string.Empty;
            try
            {
                var loc = ReadSymbolProp(layoutSymbol, "FilepathSyntaxLocation");
                var declPath = loc == null ? null : (ReadSymbolProp(loc, "SourceTree") is object tree
                    ? ReadSymbolProp(tree, "FilePath") as string
                    : null);
                if (string.IsNullOrEmpty(declPath)) return string.Empty;

                var declDir = Path.GetDirectoryName(Path.GetFullPath(declPath));
                if (declDir != null && ReportLayoutFileSystem.IsFileRelativeMarker(layoutFile))
                {
                    var fileRelativeCandidate = Path.GetFullPath(
                        Path.Combine(declDir, layoutFile.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(fileRelativeCandidate)) return fileRelativeCandidate;
                }

                var rel = layoutFile.Replace('\\', '/').TrimStart('.', '/');
                var dir = declDir;
                while (!string.IsNullOrEmpty(dir))
                {
                    if (File.Exists(Path.Combine(dir, "app.json")))
                    {
                        var candidate = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                        return File.Exists(candidate) ? Path.GetFullPath(candidate) : string.Empty;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { /* best effort — absence is handled by the consumer */ }
            return string.Empty;
        }

        /// <summary>Name → (Caption, Summary) from the emitted metadata XML's &lt;Layouts&gt; block.</summary>
        private static Dictionary<string, (string? Caption, string? Summary)> ParseLayoutCaptionsFromMetadata(string metadataXml)
        {
            var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(metadataXml);
                var layouts = doc.Root?.Element("Layouts");
                if (layouts == null) return map;
                foreach (var l in layouts.Elements("Layout"))
                {
                    var n = l.Element("Name")?.Value;
                    if (string.IsNullOrEmpty(n)) continue;
                    map[n] = (
                        l.Element("CaptionML")?.Elements("Caption").FirstOrDefault()?.Value,
                        l.Element("SummaryML")?.Elements("Summary").FirstOrDefault()?.Value);
                }
            }
            catch { /* metadata shape drift must not break the compile */ }
            return map;
        }

        /// <summary>
        /// Read the resolved implementation-codeunit ids for one AL enum value's
        /// interface implementations, ordered by interface-declaration index.
        ///
        /// The compiler resolves the value's <c>Implementation</c> property to a
        /// comma-separated list of codeunit ids (e.g. <c>"60201"</c>, or
        /// <c>"60201,60202"</c> for an enum implementing two interfaces) — the
        /// same shape the prebuilt SymbolReference JSON carries, which
        /// <see cref="AlRunner.Patches.BcAppSymbolCache"/> already parses. Capturing it
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

        /// <summary>
        /// Read one AL enum value's declared <c>Caption</c> text (issue #1775 —
        /// <c>Format(&lt;enum value&gt;)</c> and <c>FieldRef.GetEnumValueCaptionFromOrdinalValue</c>
        /// both returned the AL member name instead of this).
        ///
        /// Same symbol-level idiom as <see cref="ReadEnumValueImplementations"/>:
        /// <c>IEnumValueSymbol.GetProperty(PropertyKind)</c> resolves the property off
        /// the value's own PropertyList. Null return means "the value declares no
        /// Caption at all" (distinct from an EMPTY declared caption, <c>Caption = '';</c>
        /// — both currently resolve to the same observable string via
        /// <see cref="AlRunner.AlEnumOptionMetadata.GetCaptionFromIndex"/>'s
        /// <c>?? member name</c> fallback, but the null is preserved here so a future
        /// consumer that needs to tell them apart can).
        /// </summary>
        private static string? ReadEnumValueCaption(NavCA.IEnumValueSymbol value)
        {
            try
            {
                var caption = value.GetProperty(NavCA.PropertyKind.Caption);
                return string.IsNullOrEmpty(caption?.ValueText) ? null : caption!.ValueText;
            }
            catch
            {
                return null;
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
