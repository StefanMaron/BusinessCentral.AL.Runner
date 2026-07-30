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
// (B) NavObjectDictionary`2.get_Target — no longer hooked here.
//   The open generic has ContainsGenericParameters=true and so is not directly
//   hookable, which is why this file used to enumerate closed instantiations and
//   patch each one. That approach is gone: NclCecilRewrite now rewrites the open
//   generic's IL body once, exactly as it already did for NavObjectList`1. Only
//   the helper (NavObjectDictionary_get_Target, below) survives — Cecil calls it.
//
// (A) uses JmpHook/mprotect — no Harmony, no MonoMod.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AlRunner.Infrastructure;

namespace AlRunner;

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

    // ── (B) NavObjectDictionary`2 get_Target ───────────────────────────────
    //
    // The per-closed-instantiation JmpHook scan that used to live here is gone:
    // NavObjectDictionary`2.get_Target is now rewritten once, on the OPEN generic,
    // by NclCecilRewrite — the same treatment its sibling NavObjectList`1 already
    // had. The helper below is what that rewrite calls; only its registration
    // mechanism changed.
    //
    // The scan had to go rather than merely being redundant. It hooked CLOSED
    // instantiations, whose JmpHook keys do not match the open-form key registered
    // in NclCecilRewrite.CecilOwned — so under AL_RUNNER_ENABLE_JMPHOOK=1 the
    // coexistence guard would not have fired and both mechanisms would have patched
    // the same method, which is the documented spin-hang hazard.
    //
    // It was also incomplete by construction: it scanned a single assembly (the app
    // under test; DependencyLoader never registers dep assemblies) and only found
    // instantiations reachable as fields or properties, so a Dictionary used as a
    // method local was invisible to it. Rewriting the open generic covers every
    // instantiation in every assembly. Measured on Pageworks: +8 tests, 0 regressions,
    // and the ArgumentNullException(parent) cluster went to zero.

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
