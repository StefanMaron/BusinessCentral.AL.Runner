// ReportExecutePrintTransactionWorldTests — AlRunner#4089.
//
// A RUNNER-MECHANISM test, the counterpart of ReportRunTransactionWorldTests (#2904). The BC
// claim — Report.RunRequestPage always enters a transaction world, while Report.Execute and
// Report.Print never show a request page and so turn on the TransactionType term alone — is
// measured by corpus codeunit 60981 "Test TxModel Report Exec"
// (StefanMaron/BusinessCentral.AL.Language.Tests PR #345), whose 14 arms run on a real service
// tier. What is pinned HERE is the runner's own wiring, which the corpus cannot see:
//
//   1. The three seams exist with the shapes NclCecilRewrite.Reports.cs binds by reflection.
//      A rename or a re-signature fails the Cecil rewrite at startup with "do not commit", so
//      these assertions are what turn that into a named test failure instead.
//   2. ReportRunEntersTransactionWorld answers BC's predicate for the two arities the new
//      seams pass — useRequestForm: true unconditionally for RunRequestPage, and the report's
//      own TransactionType for Execute/Print.
//
// Not duplicated here: whether a run is actually refused or actually commits. That is the BC
// claim, it belongs upstream, and corpus 60981 is where it is measured.

using System;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ReportExecutePrintTransactionWorldTests
{
    private const int UpdateNoLocks = 0;
    private const int Update = 1;
    private const int Browse = 3;

    private readonly BcEngineFixture _engine;

    public ReportExecutePrintTransactionWorldTests(BcEngineFixture engine) => _engine = engine;

    private void Arrange(int currentTransactionType)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.ALDatabase_SetCurrentTransactionType(currentTransactionType);
        Assert.Equal(currentTransactionType, ALDatabasePatches.ALDatabase_GetCurrentTransactionType());
    }

    // --- 1. the seams the Cecil rewrite binds ------------------------------------------------

    [Fact]
    public void SyncExecuteOrPrint_HasTheShapeTheCecilRewriteBinds()
    {
        // NclCecilRewrite.Reports.cs looks this up as (object, int) and throws "do not commit"
        // when it is absent — which aborts the run rather than naming what changed.
        var m = typeof(NavReportSync).GetMethod(
            nameof(NavReportSync.SyncExecuteOrPrint), new[] { typeof(object), typeof(int) });

        Assert.NotNull(m);
        Assert.True(m!.IsStatic, "the Cecil rewrite emits `call`, so the seam must be static.");
        Assert.Equal(typeof(void), m.ReturnType);

        var ps = m.GetParameters();
        Assert.Equal(2, ps.Length);
        // Null for the static overloads, `this` for the instance ones — so it must be nullable.
        Assert.Equal(typeof(object), ps[0].ParameterType);
        Assert.Equal(typeof(int), ps[1].ParameterType);
    }

    [Fact]
    public void TheThreeReportSeams_AreDistinctMethods()
    {
        // Execute/Print must NOT be routed to SyncStaticRun: that one honours a `requestWindow`
        // argument, and Report.Execute has none — BC forces UseRequestForm = false for it.
        var execute = typeof(NavReportSync).GetMethod(
            nameof(NavReportSync.SyncExecuteOrPrint), new[] { typeof(object), typeof(int) });
        var staticRun = typeof(NavReportSync).GetMethod(
            nameof(NavReportSync.SyncStaticRun),
            new[] { typeof(int), typeof(bool), typeof(bool), typeof(object) });
        var requestPage = typeof(NavReportSync).GetMethod(
            nameof(NavReportSync.SyncRunRequestPage),
            new[] { typeof(object), typeof(int), typeof(string) });

        Assert.NotNull(execute);
        Assert.NotNull(staticRun);
        Assert.NotNull(requestPage);
        Assert.NotEqual(execute, staticRun);
        Assert.NotEqual(execute, requestPage);
    }

    [Fact]
    public void BcTransactionWorldPair_HasTheShapeReplaceBodyWithHelperRequires()
    {
        // ReplaceBodyWithHelper forwards EVERY IL argument slot and asserts helper arity
        // matches (#3328). SessionTransactionExtensions.{Begin,End}TransactionWorld each take
        // one NavSession, so each helper must take exactly one parameter — a zero-parameter
        // helper is refused at rewrite time, which is a startup abort rather than a test name.
        foreach (var name in new[] { "BcBeginTransactionWorld", "BcEndTransactionWorld" })
        {
            var m = typeof(ALDatabasePatches).GetMethod(
                name, BindingFlags.Public | BindingFlags.Static);

            Assert.True(m != null, $"ALDatabasePatches.{name} must exist for the Cecil rewrite to bind.");
            Assert.Equal(typeof(void), m!.ReturnType);
            Assert.Equal(1, m.GetParameters().Length);
            Assert.False(m.GetParameters()[0].ParameterType.IsValueType,
                "the forwarded NavSession slot is a reference, so boxing must not be required.");
        }
    }

    // --- 2. the predicate, at the arities the new seams pass ---------------------------------

    [SkippableFact]
    public void RunRequestPageSeam_EntersTheWorld_EvenForAnUpdateNoLocksReport()
    {
        // SyncRunRequestPage passes useRequestForm: true unconditionally, because BC's
        // RunRequestPageAsync is RunReportAsync(requestWindow: true, …) and that ASSIGNS
        // UseRequestForm before RunReportCoreAsync reads it. Corpus 60981 Test02/Test03.
        Arrange(UpdateNoLocks);
        Assert.True(ALDatabasePatches.ReportRunEntersTransactionWorld(
            useRequestForm: true, reportTransactionType: UpdateNoLocks));
    }

    [SkippableFact]
    public void ExecuteSeam_UpdateReport_EntersTheWorld_OnTheTransactionTypeTermAlone()
    {
        // SyncExecuteOrPrint forces UseRequestForm = false, so only the type term can fire.
        // Corpus 60981 Test06 (refused with a pending write) / Test07 (commits otherwise).
        Arrange(UpdateNoLocks);
        Assert.True(ALDatabasePatches.ReportRunEntersTransactionWorld(
            useRequestForm: false, reportTransactionType: Update));
    }

    [SkippableFact]
    public void ExecuteSeam_ReportThatHasARequestPage_DoesNotEnterTheWorld()
    {
        // The arm that distinguishes Execute from Run: report 60032 HAS a request page, and
        // Execute still does not enter a transaction world, because Execute never shows one.
        // Corpus 60981 Test09 — the whole reason the seam forces the property rather than
        // reading what the report declares.
        Arrange(UpdateNoLocks);
        Assert.False(ALDatabasePatches.ReportRunEntersTransactionWorld(
            useRequestForm: false, reportTransactionType: UpdateNoLocks));
    }

    [SkippableFact]
    public void ExecuteSeam_TransactionTypeEqualToTheSession_DoesNotEnterTheWorld()
    {
        Arrange(Browse);
        Assert.False(ALDatabasePatches.ReportRunEntersTransactionWorld(
            useRequestForm: false, reportTransactionType: Browse));
    }
}
