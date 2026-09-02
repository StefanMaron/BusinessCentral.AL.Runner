// FormPatches — NavForm.RunModalAsync short-circuit.
//
// The non-modal half used to live here too: NavFormHandle.Run (the page-variable `P.Run()`
// path) threw out-of-scope §3.11, and BcAssembler._polyfillRedirects sent the static
// Page.Run call sites to a shim that did the same. Both are gone with #2349 — non-modal
// Page.Run now runs BC's own NavForm.RunAsync, which asks NavTestExecution.TestHandleForm
// and dispatches the page to the test's TestPage.Trap() or its [PageHandler]. See
// docs/scope.md and AlRunner/Patches/RunnerModalDispatch.cs.
//
// NavForm.RunModalAsync — PAGE-REPORT-CLUSTERS §2. JmpHooks all 7 NavForm.RunModalAsync
// overloads (3 instance + 4 static) to return FormResult.OK (1) so AL Page.RunModal()
// short-circuits without hitting skeleton session state. Probe-log confirms hook fires.
//
// Hook installation: NavFormHandle.Run and NavForm.RunModalAsync via JmpHook.Apply in BcRuntime.cs.
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Dynamics.Nav.Types;
using AlRunner.Infrastructure;

namespace AlRunner;

public static class FormPatches
{
    // ──────────────────────────────────────────────────────────────────
    // NavForm.RunModalAsync — Page.RunModal() → Action.Ok (PAGE-REPORT-CLUSTERS §2)
    //
    // NavForm declares 7 RunModalAsync overloads:
    //   Instance (3): (), (NavRecord), (NavRecord, Int32)
    //   Static  (4): (bool,bool,int), (bool,bool,int,NavRecord),
    //                (bool,bool,int,NavRecord,NavFieldRef), (bool,bool,int,NavRecord,Int32)
    //
    // All replacements return FormResult.OK (1) immediately without touching skeleton
    // session state. Probe log confirms hook fires on the non-R2R path.
    // ──────────────────────────────────────────────────────────────────

    /// <summary>NavForm.RunModalAsync() — instance, 0 extra params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_0(object self)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

    /// <summary>NavForm.RunModalAsync(NavRecord) — instance, 1 extra param.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_1(object self, object record)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

    /// <summary>NavForm.RunModalAsync(NavRecord, Int32) — instance, 2 extra params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_2(object self, object record, int fieldNo)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

    /// <summary>NavForm.RunModalAsync(bool,bool,int) — static, 3 params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_S3(bool isInLookupTrigger, bool isLookup, int formId)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

    /// <summary>NavForm.RunModalAsync(bool,bool,int,NavRecord) — static, 4 params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_S4(bool isInLookupTrigger, bool isLookup, int formId, object record)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

    /// <summary>NavForm.RunModalAsync(bool,bool,int,NavRecord,NavFieldRef) — static, 5 params (fieldRef).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_S5f(bool isInLookupTrigger, bool isLookup, int formId, object record, object fieldRef)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

    /// <summary>NavForm.RunModalAsync(bool,bool,int,NavRecord,Int32) — static, 5 params (fieldNo).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavForm_RunModalAsync_S5n(bool isInLookupTrigger, bool isLookup, int formId, object record, int fieldNo)
    {
        Console.Error.WriteLine("[NavForm.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }

}
