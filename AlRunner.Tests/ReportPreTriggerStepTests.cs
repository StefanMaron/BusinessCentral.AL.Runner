// Issue #2526 — the runner ran only the FIRST of the three things BC's pre-report step does.
//
// NavReport.OnPreTriggerAsync, verbatim from BC 28.1's Ncl.dll:
//
//     await DataItemIterator.ExecuteTriggerAsync(Session, null, OnPreReportInternalAsync, "OnPreReport", this);
//     await InitializeReportLabelsAsync();
//     AddDefaultParameters();
//
// NavReportSync called only OnPreReportInternalAsync (through RunLifecycleTrigger). So the AL
// trigger ran and the other two silently did not: InitializeReportLabelsAsync is what walks
// Metadata.Labels and adds every non-PerRow label to reportDatasetParameters — the collection
// NavReport.GetParameters() returns and ReportSaveAsXmlRenderer.SerializeReportParameters
// writes into the parameters file. A column with IncludeCaption = true contributes a label
// named {ColumnName}Caption; a report's `labels` section contributes one per entry. With the
// step skipped the parameters document came out as a bare <ArrayOfReportParameter />.
//
// These are RUNNER-MECHANISM tests. The BC-behaviour claim — what a report's parameters
// document contains — is NOT expressible in the upstream corpus: the document is only
// reachable through TestRequestPage.SaveAsXml(parametersFile, dataSetFile), which takes file
// PATHS (NavTestPage.ALSaveAsXml has no stream overload), and reading a file back needs File,
// which the AL compiler rejects for Target = Cloud (AL0296) — the corpus's target. The
// end-to-end proof is therefore a measured before/after on a hermetic bundle, recorded in the
// PR, not a corpus test.
using System;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class ReportPreTriggerStepTests
{
    /// <summary>Stands in for NavReport: the whole-step method plus the bare trigger.</summary>
    public abstract class FakeReportBase
    {
        public virtual bool __IsAsync => false;
        public int WholeStepHits;
        public int BareTriggerHits;
        protected virtual ValueTask OnPreTriggerAsync() { WholeStepHits++; return default; }
        protected virtual ValueTask OnPreReportInternalAsync() { BareTriggerHits++; return default; }
        protected virtual void OnPreReport() { }
        protected virtual ValueTask OnPreReportAsync() => default;
    }

    public sealed class NormalReport : FakeReportBase { }

    /// <summary>An Ncl build without the whole-step method — the fallback path.</summary>
    public abstract class LegacyReportBase
    {
        public virtual bool __IsAsync => false;
        public int BareTriggerHits;
        protected virtual ValueTask OnPreReportInternalAsync() { BareTriggerHits++; return default; }
        protected virtual void OnPreReport() { }
        protected virtual ValueTask OnPreReportAsync() => default;
    }

    public sealed class LegacyReport : LegacyReportBase { }

    [Fact]
    public void WhenBcDeclaresTheWholeStep_ItIsSelected_NotTheBareTrigger()
    {
        var pick = NavReportSync.SelectPreTriggerMethod(typeof(FakeReportBase));

        Assert.NotNull(pick);
        Assert.Equal("OnPreTriggerAsync", pick!.Name);
        Assert.Equal(typeof(ValueTask), pick.ReturnType);
    }

    [Fact]
    public void WhenBcDeclaresTheWholeStep_RunningIt_HitsItExactlyOnce_AndNotTheBareTriggerDirectly()
    {
        var r = new NormalReport();

        NavReportSync.RunOnPreTrigger(r, typeof(FakeReportBase));

        // Exactly once: a double-dispatch would run the AL OnPreReport trigger twice, and a
        // report trigger is not idempotent.
        Assert.Equal(1, r.WholeStepHits);
        // Zero, not one: BC's own OnPreTriggerAsync is what reaches the trigger, so the runner
        // must not also call it. This is the assertion that fails if someone "helpfully" adds
        // the old RunLifecycleTrigger call back alongside the new one.
        Assert.Equal(0, r.BareTriggerHits);
    }

    /// <summary>
    /// The negative direction: an Ncl build with no OnPreTriggerAsync must still run the
    /// trigger. Degrading to the pre-#2526 behaviour is correct there; degrading to nothing
    /// would stop every report's OnPreReport.
    /// </summary>
    [Fact]
    public void WhenBcHasNoWholeStep_ItFallsBackToTheBareTrigger()
    {
        Assert.Null(NavReportSync.SelectPreTriggerMethod(typeof(LegacyReportBase)));

        var r = new LegacyReport();
        NavReportSync.RunOnPreTrigger(r, typeof(LegacyReportBase));

        Assert.Equal(1, r.BareTriggerHits);
    }
}

/// <summary>
/// Rot guard, run on every BC leg. The fix calls BC's private
/// <c>NavReport.OnPreTriggerAsync</c> and depends on it still doing all three of its steps.
/// A BC build that renames it makes <see cref="NavReportSync.SelectPreTriggerMethod"/> answer
/// null and the runner fall back SILENTLY to running only the trigger — which is exactly the
/// defect #2526 reported, reappearing with no failing test to announce it. So the members are
/// asserted here rather than rediscovered from an empty parameters document.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class ReportPreTriggerBcMembersTests
{
    private readonly BcEngineFixture _engine;
    public ReportPreTriggerBcMembersTests(BcEngineFixture engine) => _engine = engine;

    [SkippableFact]
    public void Ncl_StillDeclares_TheWholeStepAndItsThreeParts()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ncl = typeof(ITreeObject).Assembly;
        var navReport = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")!;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var wholeStep = navReport.GetMethod("OnPreTriggerAsync", F, null, Type.EmptyTypes, null);
        Assert.True(wholeStep != null,
            "NavReport.OnPreTriggerAsync() is gone from this Ncl build — RunOnPreTrigger falls back to "
            + "running only the OnPreReport trigger, so report labels and default parameters silently "
            + "stop reaching the parameters document (#2526).");
        Assert.Equal(typeof(ValueTask), wholeStep!.ReturnType);

        // Its three steps. If any is renamed the whole-step method above is still called, so the
        // runner would not notice — but the parameters document would change shape.
        Assert.NotNull(navReport.GetMethod("OnPreReportInternalAsync", F, null, Type.EmptyTypes, null));
        Assert.NotNull(navReport.GetMethod("InitializeReportLabelsAsync", F, null, Type.EmptyTypes, null));
        Assert.NotNull(navReport.GetMethod("AddDefaultParameters", F, null, Type.EmptyTypes, null));

        // AddDefaultParameters reads ResultSetProcessor.RequireDataColumnEval and NREs on a null
        // one — which is why the processor install moved ahead of the pre-report step. If this
        // property disappears the ordering constraint changes with it.
        var dataItemIterator = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.DataItemIterator")!;
        Assert.NotNull(dataItemIterator.GetProperty("ResultSetProcessor", F));
    }
}
