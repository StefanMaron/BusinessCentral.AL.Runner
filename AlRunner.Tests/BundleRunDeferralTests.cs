// BundleRunDeferralTests — the decision to run every bundle's tests after all of them have loaded (#5318).
// Pure: no runner is spawned. The end-to-end proof is JobsTddDependencyOnlyTests.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BundleRunDeferralTests
{
    private static bool Defers(int run, int listed, bool tdd, bool bundled = true, bool affected = false, bool sequential = false)
        => BundleRunDeferral.Defers(bundled, affected, sequential, run, listed, tdd);

    [Fact]
    public void SeveralRunBundles_Defer_WithOrWithoutTdd()
    {
        Assert.True(Defers(run: 2, listed: 2, tdd: false));
        Assert.True(Defers(run: 2, listed: 2, tdd: true));
        // a dependency-only folder beside several run bundles changes nothing
        Assert.True(Defers(run: 2, listed: 3, tdd: false));
        Assert.True(Defers(run: 2, listed: 3, tdd: true));
    }

    /// <summary>The defect: a worker that runs ONE bundle and compiles a dependency-only folder is a run
    /// --tdd can send round again, so it must not run in the pass that re-run discards.</summary>
    [Fact]
    public void OneRunBundleAndADependencyOnlyFolder_Defers_UnderTdd()
    {
        Assert.True(Defers(run: 1, listed: 2, tdd: true));
        Assert.True(Defers(run: 1, listed: 4, tdd: true));
    }

    /// <summary>The #5295 shape without --tdd: nothing can send the worker round again, so it keeps
    /// running in its one pass. (Deferral gives the same output for one bundle, so only this decision
    /// can pin it.)</summary>
    [Fact]
    public void OneRunBundleAndADependencyOnlyFolder_DoesNotDefer_WithoutTdd()
    {
        Assert.False(Defers(run: 1, listed: 2, tdd: false));
        Assert.False(Defers(run: 1, listed: 4, tdd: false));
    }

    [Fact]
    public void OneFolderAlone_NeverDefers()
    {
        Assert.False(Defers(run: 1, listed: 1, tdd: false));
        // no other bundle for --tdd to generate a member into
        Assert.False(Defers(run: 1, listed: 1, tdd: true));
    }

    [Fact]
    public void PerSuite_And_Affected_NeverDefer()
    {
        foreach (var tdd in new[] { false, true })
        foreach (var (run, listed) in new[] { (1, 1), (1, 2), (2, 2), (3, 4) })
        {
            Assert.False(Defers(run, listed, tdd, bundled: false));
            Assert.False(Defers(run, listed, tdd, affected: true));
        }
    }

    /// <summary>AL_RUNNER_SEQUENTIAL_BUNDLES=1 keeps one pass per bundle for several bundles, as before; it
    /// orders bundles, so a worker with one run bundle is not held back by it.</summary>
    [Fact]
    public void TheSequentialKnob_OrdersSeveralBundles_AndNotOne()
    {
        Assert.False(Defers(run: 2, listed: 2, tdd: false, sequential: true));
        Assert.False(Defers(run: 2, listed: 2, tdd: true, sequential: true));
        Assert.False(Defers(run: 2, listed: 3, tdd: true, sequential: true));
        Assert.True(Defers(run: 1, listed: 2, tdd: true, sequential: true));
        Assert.False(Defers(run: 1, listed: 2, tdd: false, sequential: true));
    }
}
