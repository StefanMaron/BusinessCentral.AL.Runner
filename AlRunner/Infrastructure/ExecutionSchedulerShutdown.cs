// ExecutionSchedulerShutdown — issue #2704, second layer.
//
// The primary fix for #2704 is the Cecil rewrite in NclCecilRewrite.Runtime.cs that marks
// BC's "BC Execution Scheduler" thread background, so a realized scheduler can no longer keep
// the process alive after Main returns. This is the tidy-shutdown layer on top of it: at the
// end of a one-shot run, if some BC-internal path realized NavEnvironment's lazy scheduler,
// dispose it so the thread leaves SchedulerLoop on its own instead of being torn down by
// process exit. It is deliberately NOT the thing correctness rests on — disposal has to be
// reached on every exit path, and the constructor rewrite does not.
//
// The one rule here: never READ `NavEnvironment.ExecutionScheduler`. The getter is
// `executionScheduler.Value`, which realizes the lazy — a shutdown helper that touched it
// would start the very thread it exists to stop. Everything below goes through the private
// LazyEx field and its IsValueCreated flag.

using System.Reflection;

namespace AlRunner.Infrastructure;

public static class ExecutionSchedulerShutdown
{
    public enum Outcome
    {
        /// <summary>Ncl was never loaded, or NavEnvironment has no instance / no lazy field (skeleton).</summary>
        NoEnvironment,
        /// <summary>The lazy exists but nothing realized it this run — nothing to dispose, nothing touched.</summary>
        NotRealized,
        /// <summary>The scheduler had been realized; its Dispose() ran.</summary>
        Disposed,
    }

    /// <summary>
    /// Dispose <c>NavEnvironment.Instance.ExecutionScheduler</c> if — and only if — it was
    /// realized during this process. Safe to call when Ncl was never loaded (compile-only
    /// runs): it looks the assembly up among those already loaded and never forces a load.
    /// </summary>
    public static Outcome DisposeIfRealized()
    {
        var ncl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Microsoft.Dynamics.Nav.Ncl", StringComparison.Ordinal));
        var envType = ncl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
        if (envType == null) return Outcome.NoEnvironment;

        // The static backing field, not the Instance property: the runner hooks get_Instance
        // and the skeleton fallback is an uninitialized object whose lazy field is null.
        var instance = envType.GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (instance == null) return Outcome.NoEnvironment;

        var lazy = envType.GetField("executionScheduler", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
        return DisposeIfRealized(lazy);
    }

    /// <summary>
    /// Testable core: <paramref name="lazyScheduler"/> is a
    /// <c>Microsoft.Dynamics.Nav.Types.LazyEx&lt;ExecutionScheduler&gt;</c> (or null). Reads
    /// only <c>IsValueCreated</c> before deciding, so an unrealized lazy stays unrealized.
    /// </summary>
    public static Outcome DisposeIfRealized(object? lazyScheduler)
    {
        if (lazyScheduler == null) return Outcome.NoEnvironment;
        var lazyType = lazyScheduler.GetType();
        var isCreated = lazyType.GetProperty("IsValueCreated", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{lazyType.FullName} has no IsValueCreated — BC LazyEx shape changed");
        if (!(bool)isCreated.GetValue(lazyScheduler)!) return Outcome.NotRealized;

        var value = lazyType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!.GetValue(lazyScheduler);
        if (value is IDisposable disposable) disposable.Dispose();
        return Outcome.Disposed;
    }
}
