// BcRuntime.LoadedRuntimeAssemblies — one memoised lookup for the BC engine assemblies,
// replacing the `AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == …)`
// idiom on paths that run per AL operation.
//
// WHY THIS IS NOT A MICRO-OPTIMISATION
//   `RuntimeAssembly.GetName()` is not a field read: it materialises an AssemblyName, which
//   calls the native `GetCodeBase()`. Doing that for every one of the ~200 assemblies loaded
//   in a run, on every call, showed up as the resolved leaf frame in 4 of 73 CPU samples of
//   the single test in #2304 — under `BcRuntime.DispatchCore` (every fired AL event) and
//   under `NavRecordRefPatches.NavRecordRef_get_Target` (every RecordRef materialisation).
//
// A MISS IS NEVER CACHED
//   Ncl is loaded LATE and from a byte array (Program.cs preloads the Cecil-rewritten copy),
//   and the Types/Common/Language assemblies only appear once ForceLoadBcDlls() has run. A
//   caller that asks before the load must get a fresh answer next time, so only a successful
//   resolution is stored. Pinned by HotPathHookCostTests.RuntimeAssemblyLookup_DoesNotCacheAMiss.
//
// ONLY FOR THE ENGINE ASSEMBLIES
//   Deliberately NOT a general assembly cache. A test-bundle assembly can be superseded by a
//   newer generation with the same simple name (see BcRuntime.IsStaleBundleAssembly and
//   EventSubscriberPatches.PruneStaleSubscribers), so memoising by simple name would pin a
//   stale generation. The BC engine assemblies are loaded once per process and never
//   replaced, which is exactly what makes memoising them safe.
using System.Collections.Concurrent;
using System.Reflection;

namespace AlRunner;

public static partial class BcRuntime
{
    private static readonly ConcurrentDictionary<string, Assembly> _runtimeAssemblies =
        new(StringComparer.Ordinal);

    private static int _runtimeAssemblyScans;

    /// <summary>How many times the AppDomain was actually walked. Test seam: the whole point
    /// of the cache is that this stops growing once an assembly has been resolved.</summary>
    internal static int RuntimeAssemblyScanCount => Volatile.Read(ref _runtimeAssemblyScans);

    internal static void ResetRuntimeAssemblyCacheForTests()
    {
        _runtimeAssemblies.Clear();
        Interlocked.Exchange(ref _runtimeAssemblyScans, 0);
    }

    /// <summary>The loaded assembly with this simple name, or null when it is not loaded yet.
    /// Successful answers are memoised; a null is not (see the file header).</summary>
    internal static Assembly? FindRuntimeAssembly(string simpleName)
    {
        if (_runtimeAssemblies.TryGetValue(simpleName, out var cached)) return cached;

        Interlocked.Increment(ref _runtimeAssemblyScans);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, simpleName, StringComparison.Ordinal)) continue;
            _runtimeAssemblies[simpleName] = asm;
            return asm;
        }
        return null;
    }

    /// <summary>As <see cref="FindRuntimeAssembly"/>, but throws naming the assembly rather
    /// than returning null — the callers below cannot proceed without it, and an NRE three
    /// frames later would not say which assembly was missing.</summary>
    internal static Assembly RequireRuntimeAssembly(string simpleName)
        => FindRuntimeAssembly(simpleName)
           ?? throw new InvalidOperationException(
               $"assembly '{simpleName}' is not loaded in this AppDomain");
}
