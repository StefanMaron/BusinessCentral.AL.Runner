using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>
/// "DLL-first" dependency-code resolver. Microsoft ships its test toolkit
/// (Tests-TestLibraries, System Application Test Library, Library Assert, …) as
/// <b>symbol-only</b> <c>.app</c> packages — AL symbols for compile, but no compiled
/// runtime code. Without this, the runner falls back to re-compiling ~hundreds of AL
/// files from source on every run (slow), or fails with NavNCLMissingMethodException.
///
/// The BC service tier itself ships those same objects <i>precompiled</i> as managed
/// assemblies under <c>apps/assembly/release/&lt;ver&gt;/&lt;sha256&gt;.dll</c> — content-addressed,
/// where the assembly name equals the file name (the sha256 hash) and cross-app
/// references are by the same hash names. Extracted once into
/// <c>~/.cache/al-runner/servicetier-dlls/&lt;ver&gt;/</c>, those DLLs expose the normal
/// BC type convention (<c>Codeunit{id}</c>, <c>Page{id}</c>, …), so the runner can run
/// the <b>real</b> Microsoft code instead of recompiling it.
///
/// This class indexes those DLLs by BC object type name (e.g. <c>Codeunit131000</c>) →
/// owning DLL, caches the index to disk, and serves an assembly on demand. Resolution is
/// lazy: only DLLs whose objects are actually invoked get loaded. A single
/// <see cref="AssemblyLoadContext.Resolving"/> handler over the cache dir serves the
/// hash-named cross-references between loaded toolkit DLLs.
///
/// See <c>.claude/rules/precompiled-dll-respect.md</c>: these are unmodified
/// MS-compiled business-logic DLLs — loaded as-is, never rewritten.
/// </summary>
public static class ServiceTierDllIndex
{
    private const int IndexFormatVersion = 1;

    // Object-type-name (e.g. "Codeunit131000") → owning DLL absolute path.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> _index = new(BuildOrLoadIndex);
    private static readonly ConcurrentDictionary<string, Assembly?> _loadedByType = new(StringComparer.Ordinal);
    private static int _resolverInstalled;

    /// <summary>Highest-version extracted-DLL cache dir, or null if none present.</summary>
    public static string? CacheDir { get; } = ResolveCacheDir();

    /// <summary>True if an extracted service-tier DLL cache is available.</summary>
    public static bool Available => CacheDir != null && _index.Value.Count > 0;

    /// <summary>True if the cache contains a DLL defining the given BC object type (e.g. "Codeunit131000").</summary>
    public static bool Contains(string objectTypeName) => _index.Value.ContainsKey(objectTypeName);

    /// <summary>
    /// Resolve a BC object type (e.g. "Codeunit131000") to its precompiled .NET type,
    /// lazily loading the owning DLL from the extracted cache. Returns null if the cache
    /// has no DLL for that object. Installs the cross-reference resolver on first hit.
    /// </summary>
    public static Type? ResolveObjectType(string objectTypeName)
    {
        if (!Available) return null;
        var asm = _loadedByType.GetOrAdd(objectTypeName, LoadOwningAssembly);
        if (asm == null) return null;
        // Metadata-backed lookup — see AlRunner/Infrastructure/AssemblyTypeIndex.cs.
        try { return AssemblyTypeIndex.For(asm).FindFirst(objectTypeName); }
        catch { return null; }
    }

    private static Assembly? LoadOwningAssembly(string objectTypeName)
    {
        if (!_index.Value.TryGetValue(objectTypeName, out var dllPath)) return null;
        EnsureCrossRefResolverInstalled();
        try
        {
            // Already loaded (by a prior hit on a sibling object in the same DLL)?
            var fileName = Path.GetFileNameWithoutExtension(dllPath);
            foreach (var a in AssemblyLoadContext.Default.Assemblies)
                if (string.Equals(a.GetName().Name, fileName, StringComparison.Ordinal))
                    return a;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[servicetier-dll] failed to load {Path.GetFileName(dllPath)} for {objectTypeName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Install a Default-ALC Resolving handler that serves the hash-named cross-references
    /// between toolkit DLLs (and any other assembly) from the extracted cache. Only fires
    /// after default resolution fails, so it never shadows framework/Nav assemblies.
    /// </summary>
    private static void EnsureCrossRefResolverInstalled()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        var dir = CacheDir;
        if (dir == null) return;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name == null) return null;
            var probe = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(probe))
                return ctx.LoadFromAssemblyPath(probe);
            return null;
        };
    }

    private static string? ResolveCacheDir()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache/al-runner/servicetier-dlls");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Select(d => (Dir: d, Ver: System.Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Ver != null)
            .OrderByDescending(t => t.Ver)
            .Select(t => t.Dir)
            .FirstOrDefault() ?? (Directory.EnumerateDirectories(root).FirstOrDefault());
    }

    private static IReadOnlyDictionary<string, string> BuildOrLoadIndex()
    {
        var dir = CacheDir;
        if (dir == null) return new Dictionary<string, string>();

        var dlls = Directory.EnumerateFiles(dir, "*.dll").ToList();
        var indexPath = Path.Combine(dir, ".object-index.json");

        // Reuse a cached index if it matches the current DLL set (count + newest mtime).
        var stamp = $"{IndexFormatVersion}|{dlls.Count}|{dlls.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max():O}";
        if (File.Exists(indexPath))
        {
            try
            {
                using var fs = File.OpenRead(indexPath);
                var cached = JsonSerializer.Deserialize<CachedIndex>(fs);
                if (cached != null && cached.Stamp == stamp)
                    return cached.Map.ToDictionary(kv => kv.Key, kv => Path.Combine(dir, kv.Value), StringComparer.Ordinal);
            }
            catch { /* rebuild on any cache read error */ }
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dll in dlls)
        {
            try
            {
                using var pe = new PEReader(File.OpenRead(dll));
                if (!pe.HasMetadata) continue;
                var mr = pe.GetMetadataReader();
                foreach (var th in mr.TypeDefinitions)
                {
                    var name = mr.GetString(mr.GetTypeDefinition(th).Name);
                    if (IsBcObjectTypeName(name))
                        map[name] = dll; // last writer wins; toolkit ids are unique across the set
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[servicetier-dll] index skip {Path.GetFileName(dll)}: {ex.Message}"); }
        }

        try
        {
            var rel = map.ToDictionary(kv => kv.Key, kv => Path.GetFileName(kv.Value));
            using var fs = File.Create(indexPath);
            JsonSerializer.Serialize(fs, new CachedIndex { Stamp = stamp, Map = rel });
        }
        catch { /* index cache is an optimization; ignore write failures */ }

        Console.Error.WriteLine($"[servicetier-dll] indexed {map.Count} objects across {dlls.Count} DLLs in {Path.GetFileName(dir)}");
        return map;
    }

    // BC object types follow "<Kind><id>" with a purely numeric id, e.g. Codeunit131000.
    private static bool IsBcObjectTypeName(string name)
    {
        int i = 0;
        while (i < name.Length && char.IsLetter(name[i])) i++;
        if (i == 0 || i == name.Length) return false;
        for (int j = i; j < name.Length; j++)
            if (!char.IsDigit(name[j])) return false;
        return true;
    }

    private sealed class CachedIndex
    {
        public string Stamp { get; set; } = "";
        public Dictionary<string, string> Map { get; set; } = new();
    }
}
