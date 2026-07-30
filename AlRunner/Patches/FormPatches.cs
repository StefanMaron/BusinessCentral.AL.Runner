// FormPatches — OOS throw sites for non-modal page run (§3.11 ui).
//
// NavFormHandle.Run implements the page-variable .Run() path. The static
// Page.Run call sites are redirected at source level via BcAssembler._polyfillRedirects
// (NavForm.Run → NavRuntimeHelpersShim.NavForm_Run) which is more reliable than
// JmpHook.Apply on .NET 8 R2R code — see BcAssembler.cs comment for details.
//
// NavFormHandle.Run:  3 instance overloads (0/1/2 extra params beyond self).
// Page variable .Run() in AL typically goes through MockFormHandle already, so
// these hooks fire mainly during BC SA init (with OosHooksActive=false → no-op).
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
    // NavFormHandle.Run — Page variable .Run() (§3.11 OOS)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>NavFormHandle.Run() — 0 extra params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavFormHandle_Run_0(object self)
    {
        if (BcRuntime.OosHooksActive)
            RunnerScope.ThrowOutOfScope("NavForm.RunAsync", "non-modal-ui", "ui");
    }

    /// <summary>NavFormHandle.Run(arg1) — 1 extra param.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavFormHandle_Run_1(object self, object arg1)
    {
        if (BcRuntime.OosHooksActive)
            RunnerScope.ThrowOutOfScope("NavForm.RunAsync", "non-modal-ui", "ui");
    }

    /// <summary>NavFormHandle.Run(arg1, arg2) — 2 extra params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavFormHandle_Run_2(object self, object arg1, object arg2)
    {
        if (BcRuntime.OosHooksActive)
            RunnerScope.ThrowOutOfScope("NavForm.RunAsync", "non-modal-ui", "ui");
    }

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

    // ──────────────────────────────────────────────────────────────────
    // NavFilterPageBuilder.RunModalAsync — FilterPageBuilder.RunModal() → Action.Ok
    // (PAGE-REPORT-CLUSTERS §3)
    //
    // NavFilterPageBuilder declares one RunModalAsync overload:
    //   Instance (1): RunModalAsync(ITreeObject parent)
    //
    // Replacement returns FormResult.OK (1) immediately without touching skeleton
    // ITreeObject/Tree/Session state. Probe log confirms hook fires.
    // ──────────────────────────────────────────────────────────────────

    /// <summary>NavFilterPageBuilder.RunModalAsync(ITreeObject) — instance, 1 param.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<FormResult> NavFilterPageBuilder_RunModalAsync(object self, object parent)
    {
        Console.Error.WriteLine("[NavFilterPageBuilder.RunModalAsync] hooked → returning Action.Ok (1)");
        return ValueTask.FromResult(FormResult.OK);
    }
}
