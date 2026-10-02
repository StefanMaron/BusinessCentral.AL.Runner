// CompilePhaseTests — #5238: among the `--jobs` workers of one shared bundle, one process takes
// the whole compile phase and the others wait for it. The runner-level proof is
// CacheCompileLockEndToEndTests; these pin the rules the call sites rely on.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// CompilePhase.Current is process-wide, and nothing else here may touch it, so the whole class is
// one sequential unit.
public sealed class CompilePhaseTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("al-runner-compile-phase-");
    private readonly string _bundle;
    private readonly UnitClaimQueue _queue;

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    public CompilePhaseTests()
    {
        Directory.CreateDirectory(_dir);
        _bundle = Path.Combine(_dir, "bundle");
        _queue = new UnitClaimQueue(_dir, _bundle);
    }

    public void Dispose()
    {
        CompilePhase.Open((UnitClaimQueue?)null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A second instance over the same queue, standing in for another worker process: its
    /// lock is a separate file handle, which is what excludes it from the first. Only the newest
    /// instance is <see cref="CompilePhase.Current"/>, and these tests hold the instances.</summary>
    private CompilePhase Another() => CompilePhase.Open(_queue)!;

    [Fact]
    public void NotAWorkerOfASharedBundle_HasNoPhase()
    {
        Assert.Null(CompilePhase.Open((UnitClaimQueue?)null));
        Assert.Null(CompilePhase.Current);
    }

    [Fact]
    public void AWorkerOfASharedBundle_HasAPhase_ThatIsTheCurrentOne()
    {
        using var phase = CompilePhase.Open(_queue);

        Assert.NotNull(phase);
        Assert.Same(phase, CompilePhase.Current);
    }

    [Fact]
    public void DisposingThePhase_ClearsCurrent_AndIsIdempotent()
    {
        var phase = CompilePhase.Open(_queue)!;
        phase.EnterToCompile("a", Long, _ => { });
        phase.Dispose();
        phase.Dispose();

        Assert.Null(CompilePhase.Current);
        Assert.False(phase.Holds);
    }

    /// <summary>Which phase a lock belongs to follows the bundle: the same bundle always names the
    /// same file, another bundle another one, and it sits in the run's claim directory.</summary>
    [Fact]
    public void ThePhaseLockIsPerBundle_AndLivesInTheClaimDirectory()
    {
        var same = new UnitClaimQueue(_dir, _bundle);
        var other = new UnitClaimQueue(_dir, Path.Combine(_dir, "other"));

        Assert.Equal(_queue.CompilePhaseLockPath, same.CompilePhaseLockPath);
        Assert.NotEqual(_queue.CompilePhaseLockPath, other.CompilePhaseLockPath);
        Assert.Equal(_dir, Path.GetDirectoryName(_queue.CompilePhaseLockPath));
    }

    /// <summary>The core: a worker that misses while another holds the phase waits for it, and learns
    /// it waited so it reads the cache again.</summary>
    [Fact]
    public void AWorkerThatMissesWhileTheLeaderHoldsThePhase_WaitsForItsEnd()
    {
        var leader = CompilePhase.Open(_queue)!;
        Assert.False(leader.EnterToCompile("dep A", Long, _ => { }));
        leader.NoteCompiling();

        var follower = Another();
        var waiting = Task.Run(() => follower.EnterToCompile("dep A", Long, _ => { }));
        Assert.False(waiting.Wait(TimeSpan.FromMilliseconds(600)),
            "the follower went on while the leader still held the compile phase");

        leader.Dispose();
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(waiting.Result, "the follower was not told it had waited");
        Assert.True(follower.Holds);
    }

    /// <summary>A follower that finds the cache filled has compiled nothing, so it gives the phase
    /// back at once: the next worker waits for a compile, never for a load.</summary>
    [Fact]
    public void AFollowerThatHits_ReleasesThePhaseForTheNextWorker()
    {
        var first = Another();
        Assert.False(first.EnterToCompile("dep A", Long, _ => { }));
        first.ReleaseIfNotCompiling();

        var next = Another();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(next.EnterToCompile("dep A", Long, _ => { }));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(next.Holds);
    }

    /// <summary>The opposite direction, which is what stops a leader being robbed mid-compile: a HIT
    /// on a later key must not release a phase this process is compiling under.</summary>
    [Fact]
    public void ACompilingHolder_KeepsThePhaseThroughAHitOnALaterKey()
    {
        var leader = CompilePhase.Open(_queue)!;
        leader.EnterToCompile("dep A", Long, _ => { });
        leader.NoteCompiling();
        leader.ReleaseIfNotCompiling();

        Assert.True(leader.Holds);
        var other = Another();
        var said = new List<string>();
        other.EnterToCompile("dep B", TimeSpan.FromMilliseconds(300), said.Add);
        Assert.False(other.Holds);
        Assert.Contains("compiling it here", Assert.Single(said), StringComparison.Ordinal);
        leader.Dispose();
    }

    /// <summary>Asking again for a phase that could not be had does not repeat the wait or the line:
    /// the worker compiles under its per-key locks, as it did before the phase existed.</summary>
    [Fact]
    public void APhaseThatCouldNotBeTaken_IsNotAskedAgain()
    {
        using var holder = CacheCompileLock.Acquire(_queue.CompilePhaseLockPath, "the phase", Long, _ => { });
        var follower = Another();
        var said = new List<string>();

        follower.EnterToCompile("dep A", TimeSpan.FromMilliseconds(300), said.Add);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var again = follower.EnterToCompile("dep B", Long, said.Add);
        clock.Stop();

        Assert.False(again);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"asked again and waited {clock.Elapsed}");
        Assert.Single(said);
    }
}
