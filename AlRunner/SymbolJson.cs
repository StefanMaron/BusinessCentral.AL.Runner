using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner;

/// <summary>
/// Writes a compile-time symbol artifact (<c>&lt;App&gt;.symbols.json</c>) from a BC
/// <see cref="Compilation"/>. Unlike a real <c>.app</c> file this contains ONLY the symbol
/// metadata (table/codeunit/page/etc. definitions) — no AL bytecode, no resources, no
/// runtime payload — so it cannot be deployed to a BC instance. Pairs with
/// <see cref="JsonSymbolReferenceLoader"/> for cross-app symbol resolution at downstream
/// compile time.
///
/// Uses the BC compiler's internal <c>SerializableSymbolModelConverter</c> via reflection
/// (no public API exists for Compilation→ModuleDefinition).
/// </summary>
public static class SymbolJsonWriter
{
    public static void WriteSymbolJson(Compilation comp, Stream output)
    {
        if (comp is null) throw new ArgumentNullException(nameof(comp));
        if (output is null) throw new ArgumentNullException(nameof(output));

        var module = GetModuleDefinition(comp);
        SymbolReferenceJsonWriter.WriteModule(output, module);
    }

    /// <summary>
    /// Converts a <see cref="Compilation"/> into the same in-memory <see cref="ModuleDefinition"/>
    /// shape <see cref="WriteSymbolJson"/> serializes, without a disk round-trip. Used both by
    /// the SymbolReference.json writer above and by <c>BcCompiler</c>'s incremental (RAD) compile
    /// path (issue #1902), which needs a <see cref="ModuleDefinition"/> to hand
    /// <c>Compilation.CreateForRad</c> as the packaged baseline for the NEXT cycle.
    ///
    /// IMPORTANT for callers building a multi-cycle baseline: this only reflects objects that
    /// were actually SOURCE-DECLARED in <paramref name="comp"/>'s own syntax trees — objects
    /// resolved from a <c>packagedModuleDefinition</c>/<c>ISymbolReferenceLoader</c> (i.e. a RAD
    /// compilation's UNCHANGED baseline objects) do NOT come back here. Converting a RAD
    /// <see cref="Compilation"/> therefore yields ONLY that cycle's delta, not the full merged
    /// module — confirmed empirically (see BcCompiler.Incremental.cs's MergeModuleDefinition).
    /// </summary>
    public static ModuleDefinition GetModuleDefinition(Compilation comp)
    {
        if (comp is null) throw new ArgumentNullException(nameof(comp));

        // Force binding so the SerializableSymbolModelConverter sees fully-resolved
        // symbols. Without this the converter returns a skeleton ModuleDefinition with
        // empty Codeunits/Tables/Pages/etc. arrays.
        _ = comp.GetDeclarationDiagnostics();
        var declaredObjects = comp.GetDeclaredApplicationObjectSymbols();
        if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
            Console.Error.WriteLine($"  DEBUG GetModuleDefinition: comp has {declaredObjects.Length} declared application object symbol(s)");

        var module = TryConvertCompilation(comp)
            ?? TryConvertCompilationByScan(comp)
            ?? throw new InvalidOperationException("Unable to build ModuleDefinition from Compilation.");

        if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
        {
            int count = 0;
            foreach (var prop in new[] { "Codeunits", "Tables", "Pages", "EnumTypes", "XmlPorts", "Reports", "Queries", "Interfaces" })
            {
                var p = typeof(ModuleDefinition).GetProperty(prop);
                if (p?.GetValue(module) is Array arr) count += arr.Length;
            }
            Console.Error.WriteLine($"  DEBUG GetModuleDefinition: ModuleDefinition has {count} object(s) populated");
        }

        return module;
    }

    private static ModuleDefinition? TryConvertCompilation(Compilation comp)
    {
        var asm = typeof(Compilation).Assembly;
        var converterType = asm.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference.SerializableSymbolModelConverter");
        return converterType == null ? null : InvokeModuleDefinitionFactory(converterType, comp);
    }

    private static ModuleDefinition? TryConvertCompilationByScan(Compilation comp)
    {
        var asm = typeof(Compilation).Assembly;
        foreach (var type in asm.GetTypes())
        {
            if (type.FullName == "Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference.SerializableSymbolModelConverter")
                continue;
            var module = InvokeModuleDefinitionFactory(type, comp);
            if (module != null) return module;
        }
        return null;
    }

    private static ModuleDefinition? InvokeModuleDefinitionFactory(Type type, Compilation comp)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        foreach (var method in methods)
        {
            if (method.ReturnType != typeof(ModuleDefinition)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 0) continue;
            if (!parameters[0].ParameterType.IsAssignableFrom(typeof(Compilation))) continue;

            object? instance = null;
            if (!method.IsStatic)
            {
                try { instance = Activator.CreateInstance(type); }
                catch { continue; }
            }
            var args = BuildArgs(parameters, comp);
            try
            {
                if (method.Invoke(instance, args) is ModuleDefinition module)
                    return module;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static object?[] BuildArgs(ParameterInfo[] parameters, Compilation comp)
    {
        var args = new object?[parameters.Length];
        args[0] = comp;
        for (var i = 1; i < parameters.Length; i++)
            args[i] = DefaultArgValue(parameters[i]);
        return args;
    }

    internal static object? DefaultArgValue(ParameterInfo p)
    {
        if (p.HasDefaultValue) return p.DefaultValue;
        var t = p.ParameterType;
        if (t == typeof(bool)) return false;
        if (t == typeof(int)) return 0;
        if (t == typeof(string)) return string.Empty;
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(t);
        }
        if (t.IsValueType) return Activator.CreateInstance(t);
        return null;
    }
}

/// <summary>
/// Symbol-reference loader backed by <c>*.symbols.json</c> files produced by
/// <see cref="SymbolJsonWriter"/>. Indexes a directory tree at construction. Returns
/// in-memory <see cref="ModuleDefinition"/>s so downstream BC compilations resolve
/// cross-app references against committed symbol artifacts (no <c>.app</c> file
/// involvement at all).
/// </summary>
/// <summary>
/// Sidecar emitted next to <c>&lt;App&gt;.symbols.json</c>. Captures the app's identity
/// and its declared dependencies (including the platform reference) so the
/// <see cref="JsonSymbolReferenceLoader"/> can answer
/// <see cref="ISymbolReferenceLoader.GetDependencies"/> at downstream compile time.
/// Without this, BC's <c>ReferenceManager</c> cannot link cross-app type references —
/// see issue #1546.
/// </summary>
public static class DepsSidecarWriter
{
    public sealed record DepEntry(string Publisher, string Name, Version Version, Guid AppId);

    /// <summary>
    /// Build the sidecar dependency closure a source dep must declare so that BC's
    /// ReferenceManager can link cross-app type references appearing in its PUBLIC surface
    /// (procedure parameters, return types, field/property types) at downstream compile time.
    /// <para>
    /// The naive list — the app.json <c>dependencies</c> array minus <c>Optional</c> entries —
    /// is WRONG: the Microsoft platform apps (Application/System/Base Application/System
    /// Application/Business Foundation) are synthesized as <c>Optional</c> implicit roots
    /// (from the manifest's <c>application</c>/<c>platform</c> fields), so filtering them out
    /// drops exactly the apps whose types most commonly appear in a signature — e.g.
    /// <c>Codeunit "Temp Blob"</c> (System Application) or <c>Enum "Copilot Capability"</c>
    /// (platform System). A dependent then sees those parameter types as
    /// <c>__MissingTypeSymbol__</c> (AL0133). See issue #1546.
    /// </para>
    /// The correct closure is what the dep actually COMPILED against: the resolved manifest
    /// set (real AppIds/versions), UNIONed with any Microsoft platform app physically present
    /// in the dep's own <c>.alpackages</c> — those enter the dep compile via the raw package
    /// scan even when they are not in the resolved spec closure. Deduped by AppId; the dep's
    /// own AppId and empty AppIds (unresolvable implicit roots) are excluded.
    /// </summary>
    public static IReadOnlyList<DepEntry> BuildClosure(
        IEnumerable<DepEntry> resolvedDeps,
        IEnumerable<DepEntry> vendoredPlatformApps,
        Guid selfAppId)
    {
        var byId = new Dictionary<Guid, DepEntry>();
        void Add(DepEntry d)
        {
            if (d.AppId == Guid.Empty || d.AppId == selfAppId) return;
            if (!byId.ContainsKey(d.AppId)) byId[d.AppId] = d;
        }
        foreach (var d in resolvedDeps) Add(d);
        foreach (var d in vendoredPlatformApps) Add(d);
        return byId.Values.ToList();
    }

    /// <summary>Write a <c>*.symbols.deps.json</c> file at <paramref name="path"/>.</summary>
    public static void Write(string path, string publisher, string name, Version version, Guid appId, IEnumerable<DepEntry> dependencies)
    {
        var depsArr = dependencies.Select(d => new
        {
            publisher = d.Publisher,
            name = d.Name,
            version = d.Version.ToString(),
            appId = d.AppId.ToString(),
        }).ToList();
        var payload = new
        {
            publisher,
            name,
            version = version.ToString(),
            appId = appId.ToString(),
            dependencies = depsArr,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(path, json);
    }
}

/// <summary>
/// Hides every package of ONE AppId from an already-built package-scanner loader, so a
/// dependency compiling its own decompiled AL source never sees its own <c>.app</c> as an
/// external reference (the AL0275 self-ambiguity that
/// <c>BcCompiler.DeduplicateAppPackageDirs(dirs, excludeAppId)</c> prevents by physically
/// dropping that <c>.app</c> from the scan set).
///
/// Why hide instead of physically dropping
/// ---------------------------------------
/// Physically dropping means a DIFFERENT scan-dir set per excluded app, which means a
/// different <c>BcCompiler.ComputeLoaderSignature</c>, which means a fresh
/// <c>MemoryCachedSymbolReferenceLoader</c> — whose whole symbol warm (every reachable
/// <c>SymbolReference.json</c> re-read and re-parsed out of its <c>.app</c>; 8–10 s on the
/// Microsoft test-library dep set) is per-instance and therefore paid again for every
/// Tier-3 dependency compile. Issue #1831: 8 dependencies × ~11.5 s ≈ 92 s per cold
/// runner-extras leg. Hiding lets ONE warm loader serve every compile.
///
/// Why hiding is equivalent to dropping
/// ------------------------------------
/// BC resolves a reference in <c>AbstractSymbolReferenceAnalyzer.FindMatchingPackageFile</c>:
/// enumerate every <c>.app</c> in the scan dirs, keep the one with the highest
/// <c>AppVersion</c> for which <c>spec.IsSatisfiedBy(publisher, name, appId, version)</c>
/// holds. Deleting a package therefore changes exactly one thing: it is no longer a
/// candidate. Refusing to answer for it is the same edit to the candidate set — PROVIDED
/// no surviving package could have become the winner in its place. That proviso is
/// checked by <see cref="CanHideInsteadOfRescan"/> before this decorator is used; when it
/// does not hold the caller falls back to a physically-reduced scan set.
///
/// The predicate used here is BC's own public <c>SymbolReferenceSpecification.IsSatisfiedBy</c>
/// — the same call BC's analyzer makes — so "would this package have answered?" is decided
/// by BC, not by a re-implementation of its matching rules.
///
/// Known, deliberate difference: BC's analyzer also emits an <c>AL1022</c>
/// ("dependency could not be found") diagnostic when a spec matches nothing. A hidden
/// package produces no such diagnostic. AL1022 is a DECLARATION diagnostic; the
/// <c>Compilation.Emit</c> path that compiles Tier-3 dependencies never inspects
/// declaration diagnostics (see BcCompiler.Emit), and no binding decision differs — the
/// module is absent either way. <c>EmitDepSymbols</c>, the one path that does inspect them,
/// compiles a source-only app that by construction has no <c>.app</c> in the scan set to
/// exclude.
/// </summary>
public sealed class SelfExcludingSymbolReferenceLoader : ISymbolReferenceLoader
{
    private readonly ISymbolReferenceLoader _inner;
    private readonly IReadOnlyList<(string Publisher, string Name, Guid AppId, Version Version)> _hidden;

    /// <param name="inner">The loader built over the FULL (superset) scan set.</param>
    /// <param name="hidden">
    /// Every package of the excluded AppId that the superset scan set contains — all of its
    /// versions, because <c>DeduplicateAppPackageDirs</c>' exclusion drops all of them.
    /// </param>
    public SelfExcludingSymbolReferenceLoader(
        ISymbolReferenceLoader inner,
        IReadOnlyList<(string Publisher, string Name, Guid AppId, Version Version)> hidden)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hidden = hidden ?? throw new ArgumentNullException(nameof(hidden));
    }

    /// <summary>
    /// True when <paramref name="reference"/> would have been answered by one of the hidden
    /// packages — i.e. exactly when deleting those packages changes this lookup's outcome.
    /// </summary>
    public bool Hides(SymbolReferenceSpecification reference)
    {
        foreach (var h in _hidden)
            if (reference.IsSatisfiedBy(h.Publisher, h.Name, h.AppId, h.Version))
                return true;
        return false;
    }

    /// <summary>
    /// Is hiding <paramref name="excludeAppId"/> observably identical to deleting its
    /// <c>.app</c> files from <paramref name="inventory"/> (the loader's scan set)?
    ///
    /// It is, unless some SURVIVING package could satisfy a spec that a hidden package
    /// satisfies — then deleting would promote that survivor to winner while hiding just
    /// answers "not found". Reading <c>SymbolReferenceSpecification.IsSatisfiedBy</c>, a
    /// package can satisfy a spec three ways: (a) <c>spec.AppId == package.AppId</c>,
    /// (b) name+publisher equality when either side's AppId is <c>Guid.Empty</c>, or
    /// (c) name equality for the Microsoft "Application"/platform special case. A survivor
    /// has a different AppId, so (a) can never make it a stand-in for a hidden package; (b)
    /// and (c) both require NAME equality. Hence the sufficient condition below: no
    /// surviving package shares a hidden package's Name, and no AppId in play is
    /// <c>Guid.Empty</c> (which would let route (b) cross name boundaries).
    /// </summary>
    public static bool CanHideInsteadOfRescan(
        IReadOnlyList<(Guid AppId, string Name)> inventory, Guid excludeAppId)
    {
        if (excludeAppId == Guid.Empty) return false;

        var hiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in inventory)
            if (e.AppId == excludeAppId) hiddenNames.Add(e.Name ?? string.Empty);
        if (hiddenNames.Count == 0) return true; // nothing to hide — trivially equivalent

        foreach (var e in inventory)
        {
            if (e.AppId == excludeAppId) continue;
            if (e.AppId == Guid.Empty) return false;
            if (hiddenNames.Contains(e.Name ?? string.Empty)) return false;
        }
        return true;
    }

    // BC's own AbstractSymbolReferenceLoader returns null (never throws) when no package in
    // the scan set matches — see its ReadFromNavAppPackage returning default(T). Returning
    // null is therefore what a physically-reduced BC loader does, and it is also the
    // "not mine" signal CompositeSymbolReferenceLoader falls through on.
    public ModuleDefinition? LoadModule(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
        => Hides(reference) ? null : _inner.LoadModule(reference, diagnostics);

    public ModuleInfo LoadModuleInfo(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics, LoadModuleInfoFlags flags)
        => Hides(reference) ? null! : _inner.LoadModuleInfo(reference, diagnostics, flags);

    // AbstractSymbolReferenceLoader.GetDependencies returns Enumerable.Empty<> when the
    // package is not in the scan set (ReadFromNavAppPackageManifest yields null → `?? Empty`).
    public IEnumerable<SymbolReferenceSpecification> GetDependencies(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
        => Hides(reference)
            ? Enumerable.Empty<SymbolReferenceSpecification>()
            : _inner.GetDependencies(reference, diagnostics);
}

/// <summary>
/// Tries each child loader in order; first one that resolves a spec wins. Lets us layer
/// the JSON-symbols loader on top of the standard package-scanner loader without
/// either replacing the other.
/// </summary>
public sealed class CompositeSymbolReferenceLoader : ISymbolReferenceLoader
{
    private readonly IReadOnlyList<ISymbolReferenceLoader> _children;
    public CompositeSymbolReferenceLoader(IReadOnlyList<ISymbolReferenceLoader> children) => _children = children;

    public ModuleDefinition? LoadModule(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
    {
        foreach (var child in _children)
        {
            try
            {
                var module = child.LoadModule(reference, diagnostics);
                if (module != null) return module;
            }
            catch (FileNotFoundException) { /* try next */ }
        }
        return null;
    }

    public IEnumerable<SymbolReferenceSpecification> GetDependencies(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
    {
        foreach (var child in _children)
        {
            try
            {
                var deps = child.GetDependencies(reference, diagnostics);
                if (deps != null) return deps;
            }
            catch (FileNotFoundException) { /* try next */ }
        }
        return Enumerable.Empty<SymbolReferenceSpecification>();
    }

    public ModuleInfo LoadModuleInfo(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics, LoadModuleInfoFlags flags)
    {
        foreach (var child in _children)
        {
            try { return child.LoadModuleInfo(reference, diagnostics, flags); }
            catch (FileNotFoundException) { /* try next */ }
        }
        throw new FileNotFoundException($"Symbol reference not found in any composed loader: {reference.Publisher}/{reference.Name} {reference.Version}");
    }
}

public sealed class JsonSymbolReferenceLoader : ISymbolReferenceLoader
{
    private readonly string _rootDirectory;
    private readonly Dictionary<string, ModuleDefinition> _moduleCache = new(StringComparer.OrdinalIgnoreCase);

    // Per-module dependency lists keyed by the same `pub|name|ver` (and `name|pub|ver`)
    // form as `_moduleCache`. Sourced from `*.symbols.deps.json` sidecars written by
    // DepCompiler. Without this, `GetDependencies` returns empty and the BC compiler's
    // ReferenceManager cannot connect cross-module type references (issue #1546).
    private readonly Dictionary<string, List<SymbolReferenceSpecification>> _dependencyCache =
        new(StringComparer.OrdinalIgnoreCase);

    public JsonSymbolReferenceLoader(string rootDirectory)
    {
        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        IndexModules();
        IndexDependencySidecars();
    }

    public bool HasAny => _moduleCache.Count > 0 || _dependencyCache.Count > 0;

    /// <summary>
    /// Re-scan the root directory, picking up files written since construction.
    ///
    /// Indexing happens in the constructor, so a loader built over an initially-empty
    /// directory would never see anything added later. Bundled mode needs exactly that:
    /// an app is emitted, its symbols are written, and the NEXT app in the same bundle
    /// must be able to reference it. Re-indexing in place keeps the loader OBJECT
    /// identity, which is what makes this cheap — BcCompiler's expensive reference
    /// loader (a filesystem scan plus a sequential symbol warm) is rebuilt only when its
    /// content signature changes, and mutating this cache does not change it.
    /// </summary>
    public void Reindex()
    {
        _moduleCache.Clear();
        _dependencyCache.Clear();
        IndexModules();
        IndexDependencySidecars();
    }

    /// <summary>
    /// Enumerate (publisher, name, version, appId) tuples for every cached module so
    /// callers can inject these specs into the BC compiler's reference list — without
    /// this, the compiler's PackageScanner only sees .app files and ignores our
    /// .symbols.json modules even though the loader has them.
    /// </summary>
    public IEnumerable<(string Publisher, string Name, Version Version, Guid AppId)> EnumerateSpecs()
    {
        foreach (var kv in _moduleCache)
        {
            var m = kv.Value;
            var publisher = GetModuleString(m, "Publisher");
            var name = GetModuleString(m, "Name");
            var version = GetModuleVersion(m);
            var appIdProp = m.GetType().GetProperty("AppId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var appId = appIdProp?.GetValue(m) is Guid g ? g : Guid.Empty;
            yield return (publisher, name, version, appId);
        }
    }

    public ModuleDefinition? LoadModule(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
    {
        if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
            Console.Error.WriteLine($"  DEBUG JsonLoader.LoadModule({reference.Publisher}/{reference.Name} v{reference.Version}) — cache has {_moduleCache.Count} module(s): {string.Join(", ", _moduleCache.Keys)}");
        if (TryGetModule(reference, out var module)) return module;
        throw new FileNotFoundException(
            $"Symbol reference not found: {reference.Publisher}/{reference.Name} {reference.Version}",
            _rootDirectory);
    }

    public IEnumerable<SymbolReferenceSpecification> GetDependencies(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
    {
        if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
            Console.Error.WriteLine($"  DEBUG JsonLoader.GetDependencies({reference.Publisher}/{reference.Name} v{reference.Version})");
        if (TryGetDependencies(reference, out var deps)) return deps;
        // This loader does not know the module at all — signal "not mine" the same way
        // LoadModule does, so CompositeSymbolReferenceLoader falls through to the next
        // child (the BC package scanner). Returning an empty list here instead would WIN
        // the composite race and erase the real dependency list of every .app module
        // (e.g. System Application → platform System), degrading cross-module types in
        // method signatures to __MissingTypeSymbol__ (AL0133) whenever any sidecar
        // symbols exist (i.e. every multi-bundle layered run).
        throw new FileNotFoundException(
            $"Symbol reference dependencies not found: {reference.Publisher}/{reference.Name} {reference.Version}",
            _rootDirectory);
    }

    public ModuleInfo LoadModuleInfo(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics, LoadModuleInfoFlags flags)
    {
        if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
            Console.Error.WriteLine($"  DEBUG JsonLoader.LoadModuleInfo({reference.Publisher}/{reference.Name} v{reference.Version})");
        if (!TryGetModule(reference, out var module))
            throw new FileNotFoundException(
                $"Symbol reference not found: {reference.Publisher}/{reference.Name} {reference.Version}",
                _rootDirectory);
        return new ModuleInfo(module, documentationProvider: null);
    }

    private void IndexModules()
    {
        if (!Directory.Exists(_rootDirectory)) return;
        foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(_rootDirectory, "*.symbols.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var module = ReadModuleDefinition(stream);
                var publisher = GetModuleString(module, "Publisher");
                var name = GetModuleString(module, "Name");
                var version = GetModuleVersion(module);
                // BC sometimes accesses spec.Publisher / spec.Name in swapped order
                // (e.g. AL1022 error messages and the loader callbacks both use the
                // reversed form). Index both orderings so the cache resolves either way.
                var keyForward = $"{publisher}|{name}|{version}";
                var keyReverse = $"{name}|{publisher}|{version}";
                if (!_moduleCache.ContainsKey(keyForward)) _moduleCache[keyForward] = module;
                if (!_moduleCache.ContainsKey(keyReverse)) _moduleCache[keyReverse] = module;
            }
            catch { /* skip unreadable */ }
        }
    }

    /// <summary>
    /// Read every <c>*.symbols.deps.json</c> sidecar under <see cref="_rootDirectory"/>
    /// and cache its declared dependencies under both `pub|name|ver` and `name|pub|ver`
    /// keys (mirroring `_moduleCache`'s ordering trick).
    /// </summary>
    private void IndexDependencySidecars()
    {
        if (!Directory.Exists(_rootDirectory)) return;
        foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(_rootDirectory, "*.symbols.deps.json"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var publisher = root.GetProperty("publisher").GetString() ?? "";
                var name = root.GetProperty("name").GetString() ?? "";
                var versionText = root.GetProperty("version").GetString() ?? "0.0.0.0";
                if (!Version.TryParse(versionText, out var version)) version = new Version(0, 0, 0, 0);

                var deps = new List<SymbolReferenceSpecification>();
                if (root.TryGetProperty("dependencies", out var depArr) &&
                    depArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var d in depArr.EnumerateArray())
                    {
                        var dPub = d.TryGetProperty("publisher", out var p) ? p.GetString() ?? "" : "";
                        var dName = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var dVerText = d.TryGetProperty("version", out var v) ? v.GetString() ?? "0.0.0.0" : "0.0.0.0";
                        if (!Version.TryParse(dVerText, out var dVer)) dVer = new Version(0, 0, 0, 0);
                        var dAppId = Guid.Empty;
                        if (d.TryGetProperty("appId", out var aid) && aid.ValueKind == System.Text.Json.JsonValueKind.String)
                            Guid.TryParse(aid.GetString(), out dAppId);
                        deps.Add(new SymbolReferenceSpecification(
                            dPub, dName, dVer, false, dAppId, false, ImmutableArray<Guid>.Empty));
                    }
                }

                var keyForward = $"{publisher}|{name}|{version}";
                var keyReverse = $"{name}|{publisher}|{version}";
                _dependencyCache[keyForward] = deps;
                _dependencyCache[keyReverse] = deps;
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
                    Console.Error.WriteLine($"  DEBUG sidecar parse failed for {file}: {ex.Message}");
            }
        }
    }

    private bool TryGetDependencies(SymbolReferenceSpecification reference, out List<SymbolReferenceSpecification> deps)
    {
        var requestedVersion = reference.Version ?? new Version(0, 0, 0, 0);
        foreach (var prefix in new[] {
            $"{reference.Publisher}|{reference.Name}|",
            $"{reference.Name}|{reference.Publisher}|",
        })
        {
            var exact = $"{prefix}{requestedVersion}";
            if (_dependencyCache.TryGetValue(exact, out deps!)) return true;

            var candidates = _dependencyCache
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Version: ParseVersionFromKey(kv.Key), Deps: kv.Value))
                .Where(c => c.Version >= requestedVersion)
                .OrderByDescending(c => c.Version)
                .ToList();
            if (candidates.Count > 0)
            {
                deps = candidates[0].Deps;
                return true;
            }
        }
        deps = null!;
        return false;
    }

    private bool TryGetModule(SymbolReferenceSpecification reference, out ModuleDefinition module)
    {
        var requestedVersion = reference.Version ?? new Version(0, 0, 0, 0);

        // BC may pass publisher/name in either order (see IndexModules comment); try both.
        foreach (var prefix in new[] {
            $"{reference.Publisher}|{reference.Name}|",
            $"{reference.Name}|{reference.Publisher}|",
        })
        {
            // Exact match first
            var exact = $"{prefix}{requestedVersion}";
            if (_moduleCache.TryGetValue(exact, out module!)) return true;

            // Version-tolerant: pick highest cached version >= requested
            var candidates = _moduleCache
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Version: ParseVersionFromKey(kv.Key), Module: kv.Value))
                .Where(c => c.Version >= requestedVersion)
                .OrderByDescending(c => c.Version)
                .ToList();
            if (candidates.Count > 0)
            {
                module = candidates[0].Module;
                return true;
            }
        }
        module = null!;
        return false;
    }

    private static Version ParseVersionFromKey(string key)
    {
        var lastBar = key.LastIndexOf('|');
        if (lastBar < 0) return new Version(0, 0, 0, 0);
        return Version.TryParse(key[(lastBar + 1)..], out var v) ? v : new Version(0, 0, 0, 0);
    }

    private static ModuleDefinition ReadModuleDefinition(Stream stream)
    {
        var asm = typeof(SymbolReferenceJsonWriter).Assembly;
        var readerType = asm.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference.SymbolReferenceJsonReader")
            ?? throw new InvalidOperationException("SymbolReferenceJsonReader type not found.");

        foreach (var method in readerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.ReturnType != typeof(ModuleDefinition)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 0) continue;
            if (!parameters[0].ParameterType.IsAssignableFrom(typeof(Stream))) continue;

            object? instance = null;
            if (!method.IsStatic) instance = Activator.CreateInstance(readerType);
            var args = new object?[parameters.Length];
            args[0] = stream;
            for (var i = 1; i < parameters.Length; i++)
                args[i] = SymbolJsonWriter.DefaultArgValue(parameters[i]);

            if (method.Invoke(instance, args) is ModuleDefinition module)
                return module;
        }
        throw new InvalidOperationException("No SymbolReferenceJsonReader method could parse ModuleDefinition.");
    }

    private static string BuildSpecKey(SymbolReferenceSpecification reference)
    {
        var version = reference.Version ?? new Version(0, 0, 0, 0);
        return $"{reference.Publisher}|{reference.Name}|{version}";
    }

    private static string BuildModuleKey(ModuleDefinition module)
    {
        var publisher = GetModuleString(module, "Publisher");
        var name = GetModuleString(module, "Name");
        var version = GetModuleVersion(module);
        return $"{publisher}|{name}|{version}";
    }

    private static string GetModuleString(ModuleDefinition module, string propertyName)
    {
        var property = module.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return property?.GetValue(module) as string ?? string.Empty;
    }

    private static Version GetModuleVersion(ModuleDefinition module)
    {
        var property = module.GetType().GetProperty("Version", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property?.GetValue(module) is string versionText
            && Version.TryParse(versionText, out var version))
            return version;
        return new Version(0, 0, 0, 0);
    }
}
