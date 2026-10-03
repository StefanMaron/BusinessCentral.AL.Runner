// CompilePhase — among the `--jobs` workers of one shared bundle, one process does ALL the
// compiling (#5238).
//
// CacheCompileLock stops two processes compiling the SAME key, but not the split: the workers
// race for each key, so one compiles some and another the rest, and both carry the compiler's
// footprint. Measurements: docs/jobs-unit-claiming.md § Cold cache.
//
// So the first worker to MISS takes this lock and keeps it until its own compile phase for the
// bundle ends (Program.cs releases it once every app of the bundle is loaded, before any test
// runs). Another worker that misses meanwhile waits for it, then reads the cache again: it is a
// HIT for everything the first one compiled. A worker that waited takes the lock only for the
// moment it takes to see the HIT, so a third worker is not queued behind a load.
//
// Scope: only the workers of a shared bundle (UnitClaimQueue), whose lock lives in the run's own
// claim directory. Unrelated runner processes sharing a cache directory still lock per key only,
// so one cold compile never holds up another run's different bundle. Lock order is always
// phase before key, so the two layers cannot wait on each other in a cycle.

namespace AlRunner.Infrastructure;

internal sealed class CompilePhase : IDisposable
{
    private readonly string _lockPath;
    private CacheCompileLock? _held;
    private bool _unusable;
    private bool _compiling;

    /// <summary>The phase of the bundle this process is loading, or null when the bundle is not
    /// shared. One bundle is loaded at a time, so this is process-wide rather than per thread.</summary>
    public static CompilePhase? Current { get; private set; }

    private CompilePhase(string lockPath) => _lockPath = lockPath;

    /// <summary>The phase for <paramref name="bundleFullPath"/> when this process is a worker of a
    /// shared bundle (the same test the claim queue uses), else null. Becomes <see cref="Current"/>.</summary>
    public static CompilePhase? Open(string bundleFullPath)
        => Open(UnitClaimQueue.ForBundle(bundleFullPath));

    internal static CompilePhase? Open(UnitClaimQueue? queue)
    {
        Current = queue == null ? null : new CompilePhase(queue.CompilePhaseLockPath);
        return Current;
    }

    public bool Holds => _held != null;

    /// <summary>
    /// This process is about to compile something: take the phase, waiting for a process that
    /// holds it. True when it had to wait, in which case the caller reads its cache entry again
    /// (a HIT when the holder compiled it) and calls <see cref="ReleaseIfNotCompiling"/>. A phase
    /// that cannot be taken (the wait expired, or the lock file is unusable) is not asked for
    /// again; the caller compiles under its per-key lock, as before.
    /// </summary>
    public bool EnterToCompile(string what, TimeSpan maxWait, Action<string> say)
    {
        if (_held != null || _unusable) return false;
        var gate = CacheCompileLock.Acquire(_lockPath, what, maxWait, say);
        if (!gate.HoldsLock)
        {
            _unusable = true;
            return gate.WaitedForSibling;
        }
        _held = gate;
        return gate.WaitedForSibling;
    }

    /// <summary>This process is going on to compile, so it keeps the phase until
    /// <see cref="Dispose"/>: a HIT on some later key must not hand it to a waiter mid-compile.</summary>
    public void NoteCompiling() => _compiling = true;

    /// <summary>The cache turned out to hold what was missed and this process has compiled nothing
    /// in the phase: the phase goes back to whoever is waiting, so the wait is the holder's
    /// compile, not its load.</summary>
    public void ReleaseIfNotCompiling()
    {
        if (_compiling) return;
        var held = _held;
        _held = null;
        held?.Dispose();
    }

    /// <summary>This process's compile publishes nothing (UncacheableCompile): hand the phase over
    /// now, and do not take it again in this bundle, so the workers behind it compile side by side
    /// as they did before the phase existed rather than one after another.</summary>
    public void ReleaseUnproductive()
    {
        _unusable = true;
        var held = _held;
        _held = null;
        held?.Dispose();
    }

    /// <summary>End of this bundle's compile phase. Idempotent.</summary>
    public void Dispose()
    {
        var held = _held;
        _held = null;
        held?.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
