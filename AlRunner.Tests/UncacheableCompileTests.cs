// UncacheableCompileTests — #5238: a compile that publishes nothing gives its locks up and leaves
// a marker, so the workers behind it do not queue for an entry that is never written. The runner-level
// proof is CacheCompileLockEndToEndTests (ABundleThatCannotBeCached..., ADependencyThatCannotBe...).

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class UncacheableCompileTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("al-runner-uncacheable-unit-");
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    public UncacheableCompileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Marker => UncacheableCompile.MarkerPath(_dir, "k1");

    private CacheCompileLock Gate() => CacheCompileLock.Acquire(Path.Combine(_dir, "k1.lock"), "thing", Short, _ => { });

    [Fact]
    public void TheMarkerIsBesideTheEntry_NamedByTheKey()
    {
        Assert.Equal(Path.Combine(_dir, "k1.uncacheable"), Marker);
        Assert.False(UncacheableCompile.Exists(Marker));
    }

    [Fact]
    public void GivingUp_LeavesTheMarker_AndFreesTheKeyLock()
    {
        var gate = Gate();
        Assert.True(gate.HoldsLock);
        var outcome = new UncacheableCompile(Marker, gate, null);

        outcome.GiveUp();

        Assert.True(UncacheableCompile.Exists(Marker));
        using var other = Gate();
        Assert.True(other.HoldsLock, "the key lock was still held after the compile gave up");
        Assert.False(other.WaitedForSibling);
    }

    [Fact]
    public void ADisposedCompileThatNeverPublished_IsAGiveUp()
    {
        var gate = Gate();
        using (new UncacheableCompile(Marker, gate, null)) { }

        Assert.True(UncacheableCompile.Exists(Marker));
        Assert.False(gate.HoldsLock);
    }

    /// <summary>The other direction: a compile that did publish leaves no marker, and removes one an
    /// earlier compile left, so a key that is cacheable again is locked again.</summary>
    [Fact]
    public void APublishedCompile_LeavesNoMarker_AndClearsAStaleOne()
    {
        File.WriteAllBytes(Marker, Array.Empty<byte>());
        var gate = Gate();
        using (var outcome = new UncacheableCompile(Marker, gate, null))
            outcome.Published();

        Assert.False(UncacheableCompile.Exists(Marker));
    }

    [Fact]
    public void GivingUp_AfterPublishing_DoesNotMarkAPublishedEntry()
    {
        using var outcome = new UncacheableCompile(Marker, null, null);
        outcome.Published();
        outcome.GiveUp();

        Assert.False(UncacheableCompile.Exists(Marker));
    }

    [Fact]
    public void GivingUp_IsIdempotent_AndAnUnwritableMarkerDoesNotThrow()
    {
        var outcome = new UncacheableCompile(Path.Combine(_dir, "no-such-dir", "k.uncacheable"), null, null);
        outcome.GiveUp();
        outcome.GiveUp();
        outcome.Dispose();
    }

    /// <summary>The phase goes with the key lock: a worker waiting on the phase is released by the
    /// same give-up.</summary>
    [Fact]
    public void GivingUp_AlsoHandsOverTheCompilePhase()
    {
        var queue = new UnitClaimQueue(_dir, Path.Combine(_dir, "bundle"));
        using var phase = CompilePhase.Open(queue)!;
        phase.EnterToCompile("thing", Short, _ => { });
        phase.NoteCompiling();
        Assert.True(phase.Holds);

        new UncacheableCompile(Marker, null, phase).GiveUp();

        Assert.False(phase.Holds);
        using var waiting = CacheCompileLock.Acquire(queue.CompilePhaseLockPath, "phase", Short, _ => { });
        Assert.True(waiting.HoldsLock);
    }
}
