// AsyncStateMachineSpike — entry-point hook for ALFieldCaptionAsync
// and closed-instantiation enumerator for NavObjectDictionary`2.get_Target.
//
// (A) Async entry-point hook (NOT MoveNext):
//   The earlier agent (§K) tried to hook the async method directly and reported
//   "hangs the test process". That agent was attempting to hook the state-machine
//   *MoveNext* method (private, struct, non-trivial ABI). This spike takes a
//   different approach: hook the *entry point* — the outer sync wrapper that
//   BC's compiler generates to create the state-machine struct and kick off the
//   first MoveNext call. The entry point:
//     ValueTask<string> ALFieldCaptionAsync(int fieldNo)
//   is a regular non-generic instance method whose FunctionPointer is directly
//   accessible. Our replacement returns ValueTask<string>.FromResult("") which
//   satisfies the awaiting callers without any state-machine creation.
//
//   Investigation confirmed (§T):
//   - smType.IsValueType = true (struct)
//   - MoveNext is private via explicit interface impl (complex to hook as struct)
//   - Entry point ALFieldCaptionAsync: FunctionPointer OK, ContainsGenericParameters=false
//   - return type == System.Threading.Tasks.ValueTask<string> (same BCL type we reference)
//
// (B) Generic via closed-instantiation enumeration:
//   NavObjectDictionary`2.get_Target has ContainsGenericParameters=true on the
//   open generic type — not directly hookable. After the test assembly is loaded,
//   we scan the loaded test assembly for closed instantiations of
//   NavObjectDictionary`2 and hook each one's get_Target individually. Set
//   AL_RUNNER_SCAN_ALL_OBJDICT=1 to restore the broader all-AppDomain scan.
//   Each closed instantiation is a non-generic type that JmpHook can patch normally.
//
// Both strategies use only JmpHook/mprotect — no Harmony, no MonoMod.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AlRunnerV2.Infrastructure;

namespace AlRunnerV2;

public static partial class BcRuntime
{
    // ── (A) ALFieldCaptionAsync entry-point hook ────────────────────────────

    /// <summary>
    /// Hooks NavRecord.ALFieldCaptionAsync(int) to return an already-completed
    /// ValueTask&lt;string&gt; with an empty string, bypassing the full field-caption
    /// metadata lookup that NREs on the skeleton session.
    ///
    /// Replacement signature: ValueTask&lt;string&gt;(object self, int fieldNo)
    /// — matches the instance-method ABI: first arg = receiver (NavRecord as object),
    ///   second arg = the int parameter.
    /// </summary>
    /// <summary>
    /// Replacement for NavRecord.ALFieldCaptionAsync(int).
    /// Returns an already-completed ValueTask&lt;string&gt; with an empty string,
    /// bypassing the full field-caption metadata lookup that NREs on the skeleton.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<string> NavRecord_ALFieldCaptionAsync(object self, int fieldNo)
        => ValueTask.FromResult(string.Empty);

    internal static void ApplyALFieldCaptionAsyncHook(Assembly navNcl)
    {
        var navRecordType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        if (navRecordType == null)
        {
            Console.Error.WriteLine("[AsyncSM] NavRecord not found — skipping");
            return;
        }

        var entryPoint = navRecordType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ALFieldCaptionAsync");

        if (entryPoint == null)
        {
            Console.Error.WriteLine("[AsyncSM] ALFieldCaptionAsync entry point not found");
            return;
        }

        if (entryPoint.ContainsGenericParameters)
        {
            Console.Error.WriteLine("[AsyncSM] ALFieldCaptionAsync has open generic params — unexpected, skipping");
            return;
        }

        Console.Error.WriteLine($"[AsyncSM] Hooking entry point: {entryPoint}");
        var repl = typeof(BcRuntime).GetMethod(nameof(NavRecord_ALFieldCaptionAsync),
            BindingFlags.Public | BindingFlags.Static)!;
        JmpHook.Apply(entryPoint, repl, "NavRecord.ALFieldCaptionAsync(int)");
        Console.Error.WriteLine("[AsyncSM] ALFieldCaptionAsync hook ACTIVE (slot-only, compiledCode patching disabled due to crash-on-write)");
    }

    // ── (B) NavObjectDictionary`2 closed-instantiation get_Target hooks ─────

    private static Type? _navObjDictOpenGeneric;  // cached open generic typedef

    /// <summary>
    /// Enumerates closed instantiations of NavObjectDictionary`2 used by the
    /// current test assembly and hooks each one's get_Target to the Option-C
    /// replacement (see NavObjectDictionary_get_Target below). Set
    /// AL_RUNNER_SCAN_ALL_OBJDICT=1 for the older all-AppDomain scan.
    /// </summary>
    internal static void ApplyNavObjectDictionaryGetTargetHooks(Assembly navNcl, Assembly? currentTestAssembly = null)
    {
        if (_navObjDictOpenGeneric == null)
        {
            _navObjDictOpenGeneric = navNcl.GetTypes()
                .FirstOrDefault(t => t.Name.StartsWith("NavObjectDictionary") && t.IsGenericTypeDefinition);
            if (_navObjDictOpenGeneric == null)
            {
                Console.Error.WriteLine("[ObjDict] NavObjectDictionary`2 open generic not found");
                return;
            }
        }

        var openGenericFqn = _navObjDictOpenGeneric.FullName!;
        int hookCount = 0;
        int skipCount = 0;
        int errCount = 0;
        var cachePath = NavObjectDictionaryHookCachePath(currentTestAssembly);
        if (TryLoadNavObjectDictionaryHookCache(cachePath, out var cachedTypeNames))
        {
            foreach (var typeName in cachedTypeNames)
            {
                var t = Type.GetType(typeName, throwOnError: false);
                if (t == null) { skipCount++; continue; }
                if (TryHookNavObjectDictionaryType(t)) hookCount++;
                else errCount++;
            }
            PerfTrace.Log($"ObjDict hook cache HIT types={cachedTypeNames.Count} hooked={hookCount} skipped={skipCount} errors={errCount}");
            return;
        }

        // Closed generic types are not surfaced via Assembly.GetTypes() — they only
        // exist when the JIT instantiates them. Discover them by scanning fields and
        // properties on all loaded types (including the test assembly emitted from AL)
        // for declared types that are closed NavObjectDictionary`2<K,V>.
        var closedTypes = new HashSet<Type>();
        var scanSw = System.Diagnostics.Stopwatch.StartNew();
        var assembliesToScan = ObjDictAssembliesToScan(currentTestAssembly).ToArray();
        foreach (var asm in assembliesToScan)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition) continue;
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                try
                {
                    foreach (var f in t.GetFields(bf))
                    {
                        var ft = f.FieldType;
                        if (ft.IsGenericType && !ft.IsGenericTypeDefinition
                            && ft.GetGenericTypeDefinition().FullName == openGenericFqn)
                            closedTypes.Add(ft);
                    }
                    foreach (var p in t.GetProperties(bf))
                    {
                        var pt = p.PropertyType;
                        if (pt.IsGenericType && !pt.IsGenericTypeDefinition
                            && pt.GetGenericTypeDefinition().FullName == openGenericFqn)
                            closedTypes.Add(pt);
                    }
                }
                catch { /* ignore type-load on unrelated types */ }
            }
        }
        scanSw.Stop();
        PerfTrace.Log($"ObjDict hook scan assemblies={assembliesToScan.Length} closedTypes={closedTypes.Count} {scanSw.ElapsedMilliseconds}ms");

        foreach (var t in closedTypes)
        {
            if (TryHookNavObjectDictionaryType(t)) hookCount++;
            else errCount++;
        }
        TryWriteNavObjectDictionaryHookCache(cachePath, closedTypes);

        Console.Error.WriteLine(
            $"[ObjDict] Scan complete: {hookCount} hooked, {skipCount} skipped, {errCount} errors");
    }

    private static bool TryHookNavObjectDictionaryType(Type t)
    {
        var getTarget = t.GetProperty("Target",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetGetMethod(true);

        if (getTarget == null || getTarget.ContainsGenericParameters)
            return false;

        try
        {
            var repl = typeof(BcRuntime).GetMethod(nameof(NavObjectDictionary_get_Target),
                BindingFlags.Public | BindingFlags.Static)!;
            JmpHook.Apply(getTarget, repl,
                $"NavObjectDictionary`2<{string.Join(",", t.GetGenericArguments().Select(a => a.Name))}>.get_Target");
            Console.Error.WriteLine($"[ObjDict] Hooked: {t.FullName}.get_Target");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ObjDict] Hook failed for {t.Name}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<Assembly> ObjDictAssembliesToScan(Assembly? currentTestAssembly)
    {
        if (Environment.GetEnvironmentVariable("AL_RUNNER_SCAN_ALL_OBJDICT") == "1")
            return AppDomain.CurrentDomain.GetAssemblies();

        var result = new List<Assembly>();
        if (currentTestAssembly != null)
            result.Add(currentTestAssembly);
        return result;
    }

    private static string NavObjectDictionaryHookCachePath(Assembly? currentTestAssembly)
    {
        var sb = new StringBuilder("v1");
        foreach (var asm in ObjDictAssembliesToScan(currentTestAssembly).OrderBy(a => a.FullName, StringComparer.Ordinal))
        {
            if (asm.IsDynamic) continue;
            if (asm != currentTestAssembly && string.IsNullOrEmpty(asm.Location)) continue;
            sb.Append('|').Append(asm.FullName);
            try { sb.Append('@').Append(asm.ManifestModule.ModuleVersionId); }
            catch { }
        }
        if (currentTestAssembly != null)
        {
            sb.Append("|current=");
            try { sb.Append(currentTestAssembly.ManifestModule.ModuleVersionId); }
            catch { sb.Append(currentTestAssembly.FullName); }
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "al-runner", "objdict-hooks", hash + ".txt");
    }

    private static bool TryLoadNavObjectDictionaryHookCache(string cachePath, out List<string> typeNames)
    {
        typeNames = new List<string>();
        try
        {
            if (!File.Exists(cachePath)) return false;
            typeNames = File.ReadAllLines(cachePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"ObjDict hook cache read failed {Path.GetFileName(cachePath)}: {ex.Message}");
            return false;
        }
    }

    private static void TryWriteNavObjectDictionaryHookCache(string cachePath, IEnumerable<Type> closedTypes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllLines(cachePath, closedTypes
                .Select(t => t.AssemblyQualifiedName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Order(StringComparer.Ordinal)!);
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"ObjDict hook cache write failed {Path.GetFileName(cachePath)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for NavObjectDictionary`2&lt;K,V&gt;.get_Target.
    ///
    /// Mirrors the original getter semantics (decompile site:
    /// Microsoft.Dynamics.Nav.Ncl.decompiled.cs:49687-49703) but substitutes
    /// the unreachable `base.Tree.Session.Company.SharedObjects` with the
    /// process-wide skeleton TreeSharedObjectContainer (same one used by
    /// NavRecordRef.get_Target in NavRecordRefPatches.cs).
    ///
    /// 1. If Tree.GetReferenceTarget() already has a SharedNavObjectDictionary
    ///    cached, return it (matches original cache hit path).
    /// 2. Otherwise construct `new SharedNavObjectDictionary&lt;TKey,TValue&gt;(container)`
    ///    via the per-closed-type cached ctor, store it via Tree.SetReferenceTarget,
    ///    and return it. The SharedNavObjectDictionary's field initializer
    ///    populates `Value = new Dictionary&lt;TKey,TValue&gt;()` so callers see a
    ///    fully-functional empty dict whose downstream Add/Get/Remove/ContainsKey
    ///    paths are real BC code.
    ///
    /// This is the Option-C polyfill per HANDOFF §5.2: hook only the cell that
    /// can't reach the real container, leave all dictionary semantics intact.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ConstructorInfo>
        _sharedNavObjectDictCtorByClosed = new();
    private static Type? _tSharedNavObjectDictionaryOpen;
    private static Type? _tITreeSharedObjectContainer;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object NavObjectDictionary_get_Target(object self)
    {
        // Look up Tree property on self type (NavComplexValue.Tree).
        var selfType = self.GetType();
        var treeProp = selfType.GetProperty("Tree",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var tree = treeProp!.GetValue(self)!;

        var treeType = tree.GetType();
        var mGet = treeType.GetMethod("GetReferenceTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null)!;
        var existing = mGet.Invoke(tree, null);
        if (existing != null) return existing;

        // Cache the open SharedNavObjectDictionary<,> type and ITreeSharedObjectContainer.
        if (_tSharedNavObjectDictionaryOpen == null)
        {
            var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            _tSharedNavObjectDictionaryOpen =
                navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavObjectDictionary`2")!;
            _tITreeSharedObjectContainer =
                navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeSharedObjectContainer")!;
        }

        // Resolve <TKey, TValue> from the closed NavObjectDictionary<,> receiver.
        var typeArgs = selfType.GetGenericArguments();
        var ctor = _sharedNavObjectDictCtorByClosed.GetOrAdd(selfType, _ =>
        {
            var closedShared = _tSharedNavObjectDictionaryOpen!.MakeGenericType(typeArgs);
            return closedShared.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { _tITreeSharedObjectContainer! }, null)!;
        });

        // Lazily build the skeleton container if NavRecordRef.get_Target hasn't yet.
        if (_skeletonSharedObjectContainer == null)
        {
            var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var tContainer = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeSharedObjectContainer")!;
            var tITree = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ITreeObject")!;
            _skeletonSharedObjectContainer = tContainer.GetConstructor(new[] { tITree })!
                .Invoke(new object?[] { RootTreeStub });
        }

        var sharedDict = ctor.Invoke(new object?[] { _skeletonSharedObjectContainer });

        var mSet = treeType.GetMethod("SetReferenceTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        mSet.Invoke(tree, new object?[] { sharedDict });
        return sharedDict;
    }
}
