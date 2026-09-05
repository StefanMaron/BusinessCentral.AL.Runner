// ExecutionSchedulerBackgroundThreadTests — issue #2704.
//
// BC's `ExecutionScheduler` constructor starts a real OS thread ("BC Execution Scheduler",
// SchedulerLoop) that is FOREGROUND by default and only ever stops when `Dispose()` is
// called — which nothing in Ncl or the runner does. `NavEnvironment.Instance.ExecutionScheduler`
// is a process-lifetime lazy that roughly ten Ncl call sites realize as a side effect (the
// captured trigger for #2704 was Base App's Feature Telemetry disposing a helper NavSession),
// so a one-shot runner process that realizes it even once prints its summary and never exits.
//
// The fix is a Cecil rewrite of the constructor (NclCecilRewrite.Runtime.cs) that marks the
// thread background before `Start()`. This test proves the rewrite landed on the Ncl the
// engine actually loaded: construct a scheduler the way NavEnvironment's lazy does, read the
// thread it started, and assert `IsBackground`. Without the rewrite the thread is foreground
// (RED); the `finally` disposes it either way so a RED run does not leave the xunit host
// hanging on exactly the thread this issue is about.

using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ExecutionSchedulerBackgroundThreadTests
{
    private readonly BcEngineFixture _engine;

    public ExecutionSchedulerBackgroundThreadTests(BcEngineFixture engine) => _engine = engine;

    [SkippableFact]
    public void SchedulerThread_IsBackground_SoItCannotOutliveMain()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ncl = typeof(ITreeObject).Assembly;
        var schedulerType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.ExecutionScheduler")!;
        var queueType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.RoundRobinSchedulingQueue")!;
        var ctor = Assert.Single(schedulerType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(6, ctor.GetParameters().Length);

        var queue = Activator.CreateInstance(queueType)!;
        // Same shape as NavEnvironment's lazy factory: createDisposed=false is the arm that
        // starts the thread; sliceLength >= MinimumSliceLength(16); sampling period > 0.
        var scheduler = (IDisposable)ctor.Invoke(new object?[] { 1, 16L, queue, false, 1000UL, null });
        try
        {
            var threadField = schedulerType.GetField("schedulerThread",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(threadField);
            var thread = Assert.IsType<Thread>(threadField!.GetValue(scheduler));
            Assert.Equal("BC Execution Scheduler", thread.Name);
            Assert.True(thread.IsAlive, "the constructor should have started SchedulerLoop");
            Assert.True(thread.IsBackground,
                "ExecutionScheduler's SchedulerLoop thread is FOREGROUND — the Cecil rewrite " +
                "(NclCecilRewrite.Runtime.cs, #2704) did not land, so a one-shot run that realizes " +
                "NavEnvironment.ExecutionScheduler will print its summary and never exit.");
        }
        finally
        {
            scheduler.Dispose();
        }
    }
}
