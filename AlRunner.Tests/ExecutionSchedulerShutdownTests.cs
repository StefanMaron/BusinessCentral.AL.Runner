// ExecutionSchedulerShutdownTests — issue #2704, second layer.
//
// Exercises ExecutionSchedulerShutdown against a REAL Microsoft.Dynamics.Nav.Types.LazyEx of
// a real ExecutionScheduler, built the same way NavEnvironment's field is — but a private
// one, so no test here disposes the engine fixture's shared NavEnvironment scheduler under
// the other engine tests. The three claims: an unrealized lazy is left unrealized (the helper
// must never read .Value), a realized one is disposed and its thread leaves SchedulerLoop, and
// a null field (the skeleton NavEnvironment) is a no-op.

using System.Linq.Expressions;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ExecutionSchedulerShutdownTests
{
    private readonly BcEngineFixture _engine;

    public ExecutionSchedulerShutdownTests(BcEngineFixture engine) => _engine = engine;

    private static Type SchedulerType => typeof(ITreeObject).Assembly
        .GetType("Microsoft.Dynamics.Nav.Runtime.ExecutionScheduler")!;

    /// <summary>A LazyEx&lt;ExecutionScheduler&gt; whose factory builds a live (createDisposed=false) scheduler.</summary>
    private static object NewLazyScheduler()
    {
        var ncl = typeof(ITreeObject).Assembly;
        var queueType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.RoundRobinSchedulingQueue")!;
        var ctor = SchedulerType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        var diagType = ctor.GetParameters()[5].ParameterType;

        var body = Expression.New(ctor,
            Expression.Constant(1), Expression.Constant(16L),
            Expression.Convert(Expression.New(queueType), ctor.GetParameters()[2].ParameterType),
            Expression.Constant(false), Expression.Constant(1000UL),
            Expression.Constant(null, diagType));
        var funcType = typeof(Func<>).MakeGenericType(SchedulerType);
        var factory = Expression.Lambda(funcType, body).Compile();

        // The exact closed type NavEnvironment's field uses (Microsoft.Dynamics.Nav.Types.LazyEx<ExecutionScheduler>).
        var lazyType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment")!
            .GetField("executionScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType;
        Assert.Equal("LazyEx`1", lazyType.Name);
        return Activator.CreateInstance(lazyType, factory)!;
    }

    private static bool IsValueCreated(object lazy) =>
        (bool)lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy)!;

    [Fact]
    public void NullField_IsNoEnvironment()
    {
        Assert.Equal(ExecutionSchedulerShutdown.Outcome.NoEnvironment,
            ExecutionSchedulerShutdown.DisposeIfRealized((object?)null));
    }

    [SkippableFact]
    public void UnrealizedLazy_IsLeftUnrealized()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var lazy = NewLazyScheduler();
        Assert.False(IsValueCreated(lazy));

        Assert.Equal(ExecutionSchedulerShutdown.Outcome.NotRealized,
            ExecutionSchedulerShutdown.DisposeIfRealized(lazy));

        // The whole point: a shutdown helper that reads .Value would START the thread here.
        Assert.False(IsValueCreated(lazy),
            "DisposeIfRealized realized the lazy — it must decide on IsValueCreated only");
    }

    [SkippableFact]
    public void RealizedLazy_IsDisposed_AndItsThreadExits()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var lazy = NewLazyScheduler();
        var scheduler = lazy.GetType().GetProperty("Value")!.GetValue(lazy)!;
        var thread = (Thread)SchedulerType.GetField("schedulerThread",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scheduler)!;
        Assert.True(thread.IsAlive);
        try
        {
            Assert.Equal(ExecutionSchedulerShutdown.Outcome.Disposed,
                ExecutionSchedulerShutdown.DisposeIfRealized(lazy));

            var disposed = (bool)SchedulerType.GetField("disposed",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scheduler)!;
            Assert.True(disposed, "ExecutionScheduler.Dispose() did not run");
            // SchedulerLoop waits at most sliceLength (16ms) per round, then sees `disposed`.
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)),
                "SchedulerLoop thread is still running 10s after Dispose()");
        }
        finally
        {
            ((IDisposable)scheduler).Dispose();
        }
    }
}
