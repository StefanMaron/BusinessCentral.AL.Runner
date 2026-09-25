// ReportOnInitReportAtConstructionTests — runner-mechanism tests for #4656 and #4665.
//
// #4656: OnInitReport now runs from NavReportSync.RunOnInitReportAtConstruction, at the end of
// every report construction route, instead of from the Run seam. These pin the rules that
// method applies: once per instance, a Quit recorded in BC's own quitCalledOnReportTrigger
// field, any other control statement swallowed, and an error left to propagate.
//
// #4665: the one-argument static NavReport.Run(int) / RunModal(int) must reach the seam that
// keeps the report's own UseRequestPage, not the one that forces a request window.
//
// The BC-behaviour claims (when OnInitReport runs, that Base Application's REPORT.Run runs it,
// that Report.Run(id) opens no request page for a UseRequestPage = false report) live in the
// upstream corpus: codeunit 60006 "Test OnInitReport Timing".

using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class ReportOnInitReportAtConstructionTests
{
    public enum FakeStatement { Skip, Quit, Break }

    // Matched by type NAME, as BC's internal NavControlException is.
    public sealed class NavControlException : Exception
    {
        public NavControlException(FakeStatement s) : base(s.ToString()) => ControlStatement = s;
        public FakeStatement ControlStatement { get; }
    }

    // Stand-in for NavReport: the sync OnInitReport virtual and BC's private Quit flag.
    public abstract class FakeReportBase
    {
        private bool quitCalledOnReportTrigger;
        public bool QuitFlag => quitCalledOnReportTrigger;
        protected virtual void OnInitReport() { }
    }

    public sealed class CountingReport : FakeReportBase
    {
        public int InitHits;
        public Exception? Throw;
        protected override void OnInitReport()
        {
            InitHits++;
            if (Throw != null) throw Throw;
        }
    }

    [Fact]
    public void OnInitReport_RunsOncePerInstance_EvenWhenConstructionCompletesTwice()
    {
        var r = new CountingReport();
        NavReportSync.RunOnInitReportAtConstruction(r, typeof(FakeReportBase));
        NavReportSync.RunOnInitReportAtConstruction(r, typeof(FakeReportBase));
        Assert.Equal(1, r.InitHits);

        var other = new CountingReport();
        NavReportSync.RunOnInitReportAtConstruction(other, typeof(FakeReportBase));
        Assert.Equal(1, other.InitHits);
    }

    [Fact]
    public void QuitInOnInitReport_SetsBcsQuitFlag_AndIsNotRethrown()
    {
        var r = new CountingReport { Throw = new NavControlException(FakeStatement.Quit) };
        NavReportSync.RunOnInitReportAtConstruction(r, typeof(FakeReportBase));
        Assert.True(r.QuitFlag);
        Assert.True(NavReportSync.ReadQuitCalledOnReportTrigger(r, typeof(FakeReportBase)));
    }

    [Fact]
    public void OtherControlStatement_IsSwallowed_WithoutSettingTheQuitFlag()
    {
        var r = new CountingReport { Throw = new NavControlException(FakeStatement.Skip) };
        NavReportSync.RunOnInitReportAtConstruction(r, typeof(FakeReportBase));
        Assert.Equal(1, r.InitHits);
        Assert.False(r.QuitFlag);
        Assert.False(NavReportSync.ReadQuitCalledOnReportTrigger(r, typeof(FakeReportBase)));
    }

    [Fact]
    public void AnErrorInOnInitReport_LeavesConstruction()
    {
        var r = new CountingReport { Throw = new InvalidOperationException("OnInitReport failed") };
        var ex = Assert.Throws<InvalidOperationException>(
            () => NavReportSync.RunOnInitReportAtConstruction(r, typeof(FakeReportBase)));
        Assert.Equal("OnInitReport failed", ex.Message);
        Assert.False(r.QuitFlag);
    }
}

/// <summary>
/// Against the real, Cecil-rewritten Ncl: the BC members RunOnInitReportAtConstruction binds,
/// and which seam each static Run/RunModal overload's rewritten body calls.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class ReportOnInitReportBcShapeTests
{
    private readonly BcEngineFixture _engine;
    public ReportOnInitReportBcShapeTests(BcEngineFixture engine) => _engine = engine;

    private static Type NavReportType => typeof(ITreeObject).Assembly
        .GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")!;

    [SkippableFact]
    public void Ncl_StillDeclares_TheQuitFlagAndControlStatement_TheFixReliesOn()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var f = NavReportType.GetField("quitCalledOnReportTrigger",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(f);
        Assert.Equal(typeof(bool), f!.FieldType);

        // The type NavReport.Quit() throws is what IsNavControlException matches by name.
        var quit = NavReportType.GetMethod("Quit", BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);
        Assert.NotNull(quit);
        var controlException = typeof(ITreeObject).Assembly.GetReferencedAssemblies()
            .Select(n => { try { return System.Reflection.Assembly.Load(n); } catch { return null; } })
            .Where(a => a != null)
            .SelectMany(a => { try { return a!.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null)!; } })
            .FirstOrDefault(x => x!.Name == "NavControlException");
        Assert.NotNull(controlException);
        var p = controlException!.GetProperty("ControlStatement",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(p);
        Assert.True(p!.PropertyType.IsEnum);
        Assert.Contains("Quit", Enum.GetNames(p.PropertyType));
    }

    [SkippableTheory]
    [InlineData("Run", 1, nameof(NavReportSync.SyncStaticRunKeepRequestWindow))]
    [InlineData("RunModal", 1, nameof(NavReportSync.SyncStaticRunKeepRequestWindow))]
    [InlineData("Run", 2, nameof(NavReportSync.SyncStaticRun))]
    [InlineData("RunModal", 2, nameof(NavReportSync.SyncStaticRun))]
    [InlineData("Run", 3, nameof(NavReportSync.SyncStaticRun))]
    public void StaticRunOverload_CallsTheSeamThatMatchesItsRequestWindow(
        string methodName, int arity, string expectedSeam)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var m = NavReportType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null,
            Enumerable.Repeat(typeof(bool), arity - 1).Prepend(typeof(int)).ToArray(), null);
        Assert.NotNull(m);

        var il = m!.GetMethodBody()!.GetILAsByteArray()!;
        var called = new List<string>();
        for (int i = 0; i < il.Length; i++)
        {
            if (il[i] != 0x28 || i + 4 >= il.Length) continue; // `call <token>`
            var token = BitConverter.ToInt32(il, i + 1);
            try { called.Add(m.Module.ResolveMethod(token)!.Name); }
            catch (ArgumentException) { }
            i += 4;
        }
        Assert.Equal(new[] { expectedSeam }, called);
    }
}
