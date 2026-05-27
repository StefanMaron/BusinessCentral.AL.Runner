// StaleSymbolUpgrader — detects stale function IDs in symbol-only .app packages
// and registers stale→current mappings so the post-compile IL patcher can fix
// compiled assemblies.
//
// Background
// ----------
// ISV/partner projects sometimes vendor an old BC version's Microsoft symbol-only
// .app file (e.g. Microsoft_Tests-TestLibraries.app v17.0) inside their
// .alpackages/ folder. When the runner compiles the ISV's test assembly, BC's
// compiler reads the stale SymbolReference.json and bakes the OLD function IDs
// into the emitted C# (the per-method numeric identifiers used by BC's
// `OnInvokeAsync` dispatch switch). At runtime, the current BC DLLs (e.g. BC 28.1)
// only know the CURRENT IDs → every method call hits the default branch →
// NavNCLCompilationException "object does not have a member with that ID".
//
// Fix
// ---
// When DependencyLoader encounters a symbol-only .app (no AL source) this class:
//  1. Extracts SymbolReference.json from the .app NAVX ZIP.
//  2. Parses it as JsonNode.
//  3. For each codeunit, looks up the corresponding precompiled .NET type via
//     ServiceTierDllIndex (the same DLLs CodeunitPatches uses for lazy dispatch).
//  4. Reflects over the .NET type's methods: each carries [MethodId(int)] and
//     [NavName("string")] custom attributes. Builds a name→current-id map.
//  5. Walks the "Methods" array in the JSON and records (staleId, currentId) pairs.
//  6. Stores them in a global static StaleToCurrentIds registry.
// Then StaleFunctionIdPatcher (called post-compile in BcAssembler) replaces the
// stale IDs in the compiled assembly's IL.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace AlRunnerV2.Infrastructure;

public static class StaleSymbolUpgrader
{
    // Global stale→current ID mapping, populated by TryRegisterIds.
    // Key = stale function ID (from old BC version symbol).
    // Value = current function ID (from BC 28.1 service-tier DLL reflection).
    private static readonly ConcurrentDictionary<int, int> _staleToCurrentIds = new();

    // Cache: cacheKey → whether already processed this package.
    private static readonly ConcurrentDictionary<string, bool> _processed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Snapshot of all known stale→current function ID mappings.</summary>
    public static IReadOnlyDictionary<int, int> StaleToCurrentIds => _staleToCurrentIds;

    /// <summary>
    /// Inspect <paramref name="appPath"/> (a symbol-only .app) and register all
    /// known stale→current function ID mappings into <see cref="StaleToCurrentIds"/>.
    /// No-op if the service-tier DLL cache is unavailable, if the package is already
    /// current, or if the package was already processed.
    /// </summary>
    public static void TryRegisterIds(string appPath, AppManifest manifest)
    {
        if (!ServiceTierDllIndex.Available)
            return;

        var cacheKey = $"{appPath}|{TryGetLastWriteUtc(appPath):O}";
        if (!_processed.TryAdd(cacheKey, true))
            return; // already done

        DoRegisterIds(appPath, manifest);
    }

    private static void DoRegisterIds(string appPath, AppManifest manifest)
    {
        var srBytes = AppLoader.ExtractSymbolReferenceBytes(appPath);
        if (srBytes == null)
            return;

        JsonNode? root;
        try
        {
            // Strip UTF-8 BOM if present.
            var span = srBytes.AsSpan();
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                span = span[3..];
            root = JsonNode.Parse(span);
            if (root == null) return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[stale-symbol] {manifest.Publisher}/{manifest.Name}: cannot parse SymbolReference.json: {ex.Message}");
            return;
        }

        var codeunitsNode = root["Codeunits"]?.AsArray();
        if (codeunitsNode == null || codeunitsNode.Count == 0)
            return;

        int totalRegistered = 0;
        int totalUncovered = 0;

        foreach (var cuNode in codeunitsNode)
        {
            if (cuNode == null) continue;
            var cuId = cuNode["Id"]?.GetValue<int>();
            if (cuId == null) continue;

            var typeName = $"Codeunit{cuId}";
            var bcType = ServiceTierDllIndex.ResolveObjectType(typeName);
            if (bcType == null)
            {
                totalUncovered++;
                continue;
            }

            var currentIdMap = BuildMethodIdMap(bcType);
            var methodsNode = cuNode["Methods"]?.AsArray();
            if (methodsNode == null) continue;

            foreach (var mNode in methodsNode)
            {
                if (mNode == null) continue;
                var mName = mNode["Name"]?.GetValue<string>();
                if (mName == null) continue;
                if (!currentIdMap.TryGetValue(mName, out var currentId)) continue;
                var staleId = mNode["Id"]?.GetValue<int>();
                if (staleId == null || staleId == currentId) continue; // already correct

                _staleToCurrentIds[staleId.Value] = currentId;
                totalRegistered++;
            }
        }

        if (totalRegistered > 0)
            Console.Error.WriteLine(
                $"[stale-symbol] {manifest.Publisher}/{manifest.Name} v{manifest.Version}: " +
                $"registered {totalRegistered} stale ID mapping(s)" +
                (totalUncovered > 0 ? $" ({totalUncovered} codeunit(s) NOT in DLL cache — keep stale IDs)" : ""));
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static Dictionary<string, int> BuildMethodIdMap(Type bcType)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var methods = bcType.GetMethods(
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static);

        foreach (var m in methods)
        {
            int? methodId = null;
            string? navName = null;

            foreach (var attr in m.GetCustomAttributesData())
            {
                var typeName = attr.AttributeType.Name;
                if (typeName == "MethodIdAttribute" && attr.ConstructorArguments.Count >= 1)
                    methodId = (int)attr.ConstructorArguments[0].Value!;
                else if (typeName == "NavNameAttribute" && attr.ConstructorArguments.Count >= 1)
                    navName = attr.ConstructorArguments[0].Value as string;
            }

            if (methodId.HasValue && navName != null)
                map[navName] = methodId.Value;
        }
        return map;
    }

    /// <summary>
    /// Directly injects a stale→current mapping — for unit tests only.
    /// Does not guard against already-processed state because tests start fresh.
    /// </summary>
    internal static void InjectMappingForTest(int staleId, int currentId)
        => _staleToCurrentIds[staleId] = currentId;

    /// <summary>Clears all registered mappings — for unit tests only.</summary>
    internal static void ClearMappingsForTest()
        => _staleToCurrentIds.Clear();

    private static DateTime TryGetLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
