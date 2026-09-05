// PageLifecycleTriggerDispatchTests — the page twin of ReportLifecycleTriggerDispatchTests.
//
// The page-level lifecycle triggers of a PRECOMPILED dependency page (Base Application etc.)
// never ran: BC's compiler emits a page trigger either as a synchronous `OnOpenPage()`
// override or as `OnOpenPageAsync()` with `__IsAsync => true`, both are virtuals on NavForm
// with EMPTY base bodies, and RunnerPageInstance.InvokeRecordTrigger resolved the SYNC name
// by reflection. That lookup never fails — it just binds NavForm's empty base method on every
// page that emitted the async flavour, so the trigger ran and did nothing.
//
// The runner's own AL emit produces the sync flavour, which is why every runner-authored page
// worked and every Base Application page did not. Measured on page 981 "Payment Registration":
// its OnOpenPage calls RunSetup(), which opens the modal setup page that creates the current
// user's row; none of it ran, and Tests-ERM codeunit 134710 lost 47 tests to a row that was
// never created (issue #2729).
//
// The fix dispatches through BC's own NavForm.RaiseOn{trigger}Async, which applies the
// __IsAsync rule AND runs every pageextension's copy of the trigger AND raises the trigger's
// integration event. This file is the ROT GUARD for that reach: a BC service update that
// renames or removes one of those dispatchers sends InvokeRecordTrigger back to the sync-only
// lookup, silently — the same shape that lost 53 tests once before. The end-to-end proof lives
// in tests/runner-extras/microsoft-dependencies (PrecompiledPage_* tests), which runs on every
// BC leg. The upstream corpus cannot express this defect at all: every corpus page is compiled
// by the runner's own emit path, which produces the sync flavour.

using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class PageLifecycleTriggerDispatchTests
{
    private readonly BcEngineFixture _engine;
    public PageLifecycleTriggerDispatchTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags F =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Every lifecycle trigger RunnerPageInstance drives, with the parameter types it passes
    /// and the return type the caller depends on. The two ValueTask&lt;bool&gt; rows are the
    /// load-bearing ones: OnQueryClosePage's veto and OnInsertRecord's veto are read from the
    /// dispatcher's result, so a shape change there does not merely skip a trigger, it turns a
    /// refusal into an approval.
    /// </summary>
    public static TheoryData<string, Type[], Type> Dispatchers() => new()
    {
        { "RaiseOnOpenPageAsync",           Type.EmptyTypes,               typeof(ValueTask) },
        { "RaiseOnClosePageAsync",          Type.EmptyTypes,               typeof(ValueTask) },
        { "RaiseOnAfterGetRecordAsync",     Type.EmptyTypes,               typeof(ValueTask) },
        { "RaiseOnAfterGetCurrRecordAsync", Type.EmptyTypes,               typeof(ValueTask) },
        { "RaiseOnModifyRecordAsync",       Type.EmptyTypes,               typeof(ValueTask<bool>) },
        { "RaiseOnNewRecordAsync",          new[] { typeof(bool) },        typeof(ValueTask) },
        { "RaiseOnInsertRecordAsync",       new[] { typeof(bool) },        typeof(ValueTask<bool>) },
        { "RaiseOnQueryClosePageAsync",     new[] { typeof(FormResult) },  typeof(ValueTask<bool>) },
    };

    [SkippableTheory]
    [MemberData(nameof(Dispatchers))]
    public void Ncl_NavForm_StillDeclares_TheDispatcher_InvokeRecordTriggerReachesFor(
        string name, Type[] parameterTypes, Type returnType)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var navForm = typeof(ITreeObject).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavForm")!;

        var m = navForm.GetMethod(name, F, null, parameterTypes, null);
        Assert.True(m != null,
            $"NavForm.{name} is gone from this Ncl build — RunnerPageInstance.InvokeRecordTrigger "
            + "falls back to the sync-only virtual, which is the empty base body on every "
            + "precompiled page. That failure is silent (issue #2729).");
        Assert.Equal(returnType, m!.ReturnType);
    }

    /// <summary>
    /// The property the dispatchers branch on, and the sync/async virtual PAIR they branch
    /// between. Both flavours must exist as independent virtuals — if BC ever made the sync one
    /// forward to the async one, resolving the sync name would stop being a defect and this
    /// whole fix would be redundant; if it removed one, the branch has nothing to pick.
    /// </summary>
    [SkippableFact]
    public void Ncl_NavForm_StillDeclares_BothTriggerFlavours_AndTheIsAsyncFlag()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var navForm = typeof(ITreeObject).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavForm")!;

        foreach (var name in new[] { "OnOpenPage", "OnClosePage", "OnAfterGetRecord" })
        {
            Assert.True(navForm.GetMethod(name, F, null, Type.EmptyTypes, null) != null,
                $"NavForm.{name}() (the sync flavour) is gone.");
            Assert.True(navForm.GetMethod(name + "Async", F, null, Type.EmptyTypes, null) != null,
                $"NavForm.{name}Async() (the async flavour) is gone.");
        }

        var isAsync = navForm.GetProperty("__IsAsync", F);
        Assert.True(isAsync != null,
            "NavApplicationObjectBase.__IsAsync is gone — BC's own RaiseOn*Async dispatchers "
            + "would have nothing to branch on and every page would run as the sync flavour.");
        Assert.Equal(typeof(bool), isAsync!.PropertyType);
    }

    /// <summary>
    /// The naming convention the fix depends on: "Raise" + the trigger name + "Async". Asserted
    /// against the same trigger names RunnerPageInstance passes to InvokeRecordTrigger, so a
    /// rename on either side shows up here rather than as a page that quietly stops working.
    /// </summary>
    [SkippableFact]
    public void EveryTriggerNameTheRunnerDrives_HasADispatcherUnderTheRaiseAsyncConvention()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var navForm = typeof(ITreeObject).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavForm")!;

        string[] driven =
        {
            "OnOpenPage", "OnQueryClosePage", "OnClosePage", "OnAfterGetRecord",
            "OnAfterGetCurrRecord", "OnNewRecord", "OnInsertRecord", "OnModifyRecord",
        };

        foreach (var trigger in driven)
            Assert.True(navForm.GetMethod("Raise" + trigger + "Async", F) != null,
                $"No NavForm.Raise{trigger}Async — InvokeRecordTrigger would resolve the bare "
                + $"'{trigger}' virtual, which is empty on every precompiled page.");
    }
}
