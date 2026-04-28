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

    public JsonSymbolReferenceLoader(string rootDirectory)
    {
        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        IndexModules();
    }

    public bool HasAny => _moduleCache.Count > 0;

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
                var key = BuildModuleKey(module);
                if (!_moduleCache.ContainsKey(key))
                    _moduleCache[key] = module;
            }
            catch { /* skip unreadable */ }
        }
    }

    private bool TryGetModule(SymbolReferenceSpecification reference, out ModuleDefinition module)
    {
        // Exact match first
        var key = BuildSpecKey(reference);
        if (_moduleCache.TryGetValue(key, out module!)) return true;

        // Version-tolerant fallback: match by Publisher+Name, then prefer the highest cached
        // version that is >= the requested version (BC dep declarations typically use a
        // less-specific version like 27.5.0.0 while the actual emitted package uses the
        // build-specific 27.5.46862.49619).
        var requestedVersion = reference.Version ?? new Version(0, 0, 0, 0);
        var prefix = $"{reference.Publisher}|{reference.Name}|";
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
