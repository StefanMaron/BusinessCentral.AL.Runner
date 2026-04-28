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

        // Force binding so the SerializableSymbolModelConverter sees fully-resolved
        // symbols. Without this the converter returns a skeleton ModuleDefinition with
        // empty Codeunits/Tables/Pages/etc. arrays.
        _ = comp.GetDeclarationDiagnostics();
        var declaredObjects = comp.GetDeclaredApplicationObjectSymbols();
        if (Environment.GetEnvironmentVariable("ALRUNNER_DUMP_SYMBOLS") == "1")
            Console.Error.WriteLine($"  DEBUG WriteSymbolJson: comp has {declaredObjects.Length} declared application object symbol(s)");

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
            Console.Error.WriteLine($"  DEBUG WriteSymbolJson: ModuleDefinition has {count} object(s) populated");
        }

        SymbolReferenceJsonWriter.WriteModule(output, module);
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
        return Enumerable.Empty<SymbolReferenceSpecification>();
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
        foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.symbols.json", SearchOption.AllDirectories))
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
        foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.symbols.deps.json", SearchOption.AllDirectories))
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
