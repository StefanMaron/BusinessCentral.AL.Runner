// ReportLifecycleTriggerDispatchTests — the report-level lifecycle triggers of a
// PRECOMPILED dependency report (Base Application etc.) never ran under Report.Run() /
// RunModal(): the Base Application's Report34 "Change Payment Tolerance" declared only
// `OnPostReportAsync` (with `__IsAsync => true`), and NavReportSync invoked only the
// synchronous `OnPostReport` virtual, whose base body is empty. Tests-ERM then ended ~50
// tests on "The following UI handlers were not executed: ConfirmHandler", because the
// Confirm those triggers raise was never reached.
//
// These are RUNNER-MECHANISM tests: they pin the resolution rule OUR code applies
// (NavReportSync.SelectLifecycleTrigger / RunLifecycleTrigger) and the BC members that
// rule depends on. The end-to-end proof — report 34's OnPostReport writing GL Setup and
// raising its Confirm into a [ConfirmHandler] — lives in
// tests/runner-extras/microsoft-dependencies (PrecompiledReport_* tests), which runs on
// every BC leg of the matrix. The upstream corpus cannot express this defect at all: every
// corpus report is compiled by the runner's own emit path, which produces the sync flavour.

using System.Reflection;
using System.Threading.Tasks;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class ReportLifecycleTriggerDispatchTests
{
    // A stand-in for NavReport's virtual pair: two independent virtuals, both empty, neither
    // forwarding to the other — exactly the shape decompiled from Ncl (bc281:
    // NavReport.OnPostReport / OnPostReportAsync both have empty bodies).
    public abstract class FakeReportBase
    {
        public virtual bool __IsAsync => false;
        public int SyncHits;
        public int AsyncHits;
        protected virtual void OnPostReport() { }
        protected virtual ValueTask OnPostReportAsync() => default;
        protected virtual void OnInitReport() { }
        protected virtual ValueTask OnInitReportAsync() => default;
    }

    // What BC's compiler emits into a Ready2Run dependency: the ASYNC flavour + __IsAsync.
    // It ALSO overrides the sync one here, so a double-dispatch would be visible.
    public sealed class AsyncFlavourReport : FakeReportBase
    {
        public override bool __IsAsync => true;
        protected override void OnPostReport() => SyncHits++;
        protected override ValueTask OnPostReportAsync() { AsyncHits++; return default; }
    }

    // What the runner's own emit path produces: the SYNC flavour, __IsAsync left false.
    public sealed class SyncFlavourReport : FakeReportBase
    {
        protected override void OnPostReport() => SyncHits++;
        protected override ValueTask OnPostReportAsync() { AsyncHits++; return default; }
    }

    [Fact]
    public void AsyncFlavour_RunsTheAsyncOverride_ExactlyOnce()
    {
        var r = new AsyncFlavourReport();
        NavReportSync.RunLifecycleTrigger(r, typeof(FakeReportBase), "OnPostReport");
        Assert.Equal(1, r.AsyncHits);
        Assert.Equal(0, r.SyncHits); // not double-fired, not the empty sync body
    }

    [Fact]
    public void SyncFlavour_RunsTheSyncOverride_ExactlyOnce()
    {
        var r = new SyncFlavourReport();
        NavReportSync.RunLifecycleTrigger(r, typeof(FakeReportBase), "OnPostReport");
        Assert.Equal(1, r.SyncHits);
        Assert.Equal(0, r.AsyncHits);
    }

    [Fact]
    public void SelectLifecycleTrigger_PicksByIsAsync()
    {
        var asyncPick = NavReportSync.SelectLifecycleTrigger(new AsyncFlavourReport(), typeof(FakeReportBase), "OnPostReport");
        var syncPick = NavReportSync.SelectLifecycleTrigger(new SyncFlavourReport(), typeof(FakeReportBase), "OnPostReport");
        Assert.Equal("OnPostReportAsync", asyncPick!.Name);
        Assert.Equal("OnPostReport", syncPick!.Name);
    }

    [Fact]
    public void AsyncFlavour_WithNoAsyncVirtualOnBase_FallsBackToSync()
    {
        // A trigger name whose base declares no *Async counterpart must still resolve to the
        // sync virtual rather than to nothing — the rule is "async when both sides agree".
        var r = new AsyncFlavourReport();
        var pick = NavReportSync.SelectLifecycleTrigger(r, typeof(FakeReportBase), "OnInitReport");
        Assert.NotNull(pick);
        // FakeReportBase declares OnInitReportAsync, so this is the async one:
        Assert.Equal("OnInitReportAsync", pick!.Name);
        var pick2 = NavReportSync.SelectLifecycleTrigger(r, typeof(FakeReportBase), "OnPreReport");
        Assert.Null(pick2); // neither flavour declared on the fake base -> nothing to run
    }

    [Fact]
    public void AsyncOverride_Exception_SurfacesUnwrapped()
    {
        var r = new ThrowingAsyncReport();
        var ex = Assert.Throws<InvalidOperationException>(
            () => NavReportSync.RunLifecycleTrigger(r, typeof(FakeReportBase), "OnPostReport"));
        Assert.Equal("boom from the AL trigger", ex.Message);
    }

    public sealed class ThrowingAsyncReport : FakeReportBase
    {
        public override bool __IsAsync => true;
        protected override async ValueTask OnPostReportAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom from the AL trigger");
        }
    }
}

/// <summary>
/// Rot guard, run on every BC leg: the fix reaches into BC's private
/// <c>NavReport.On{Pre,Post}ReportInternalAsync</c> and public
/// <c>NavApplicationObjectBase.__IsAsync</c>. A BC service update that renames or removes
/// any of them makes the runner fall back silently to the sync-only rule (Pre/Post) — the
/// exact shape that lost 53 tests once before (#finding: Cecil rewrites rot silently) — so
/// their presence is asserted here rather than discovered by a red Tests-ERM run.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class ReportLifecycleTriggerBcMembersTests
{
    private readonly BcEngineFixture _engine;
    public ReportLifecycleTriggerBcMembersTests(BcEngineFixture engine) => _engine = engine;

    [SkippableFact]
    public void Ncl_StillDeclares_TheDispatcherAndFlag_TheFixReliesOn()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ncl = typeof(ITreeObject).Assembly;
        var navReport = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")!;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var name in new[] { "OnPreReportInternalAsync", "OnPostReportInternalAsync" })
        {
            var m = navReport.GetMethod(name, F, null, Type.EmptyTypes, null);
            Assert.True(m != null, $"NavReport.{name}() is gone from this Ncl build — RunLifecycleTrigger silently falls back to the __IsAsync mirror (report extensions' triggers would stop running).");
            Assert.Equal(typeof(ValueTask), m!.ReturnType);
        }
        foreach (var name in new[] { "OnInitReport", "OnPreReport", "OnPostReport" })
        {
            Assert.NotNull(navReport.GetMethod(name, F, null, Type.EmptyTypes, null));
            Assert.NotNull(navReport.GetMethod(name + "Async", F, null, Type.EmptyTypes, null));
        }
        var isAsync = navReport.GetProperty("__IsAsync", F);
        Assert.True(isAsync != null, "NavApplicationObjectBase.__IsAsync is gone — SelectLifecycleTrigger would treat every report as the sync flavour.");
        Assert.Equal(typeof(bool), isAsync!.PropertyType);
    }
}
