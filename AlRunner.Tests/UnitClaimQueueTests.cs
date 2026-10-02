// UnitClaimQueueTests — the first-come, first-served claim that lets several `--jobs` workers
// share one bundle (#5130). A claim that succeeds twice runs a test codeunit on two workers and
// reports each of its tests twice; one that fails for the wrong reason drops the codeunit.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class UnitClaimQueueTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("unitclaim-");

    public UnitClaimQueueTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void TryClaim_SucceedsExactlyOnce_AcrossQueuesSharingADirectory()
    {
        var worker1 = new UnitClaimQueue(_dir, "/b/erm");
        var worker2 = new UnitClaimQueue(_dir, "/b/erm");

        Assert.True(worker1.TryClaim("App", "Codeunit50100"));
        Assert.False(worker2.TryClaim("App", "Codeunit50100"));
        Assert.False(worker1.TryClaim("App", "Codeunit50100"));
    }

    /// <summary>Under many threads, one winner: the exclusivity is the file system's, not a lucky
    /// ordering of two calls.</summary>
    [Fact]
    public void TryClaim_HasOneWinner_UnderContention()
    {
        var wins = 0;
        Parallel.For(0, 64, _ =>
        {
            if (new UnitClaimQueue(_dir, "/b/erm").TryClaim("App", "Codeunit50100"))
                Interlocked.Increment(ref wins);
        });

        Assert.Equal(1, wins);
    }

    /// <summary>The claim names the codeunit within its bundle AND app group: two bundles that both
    /// declare Codeunit50000 are different units, and must not shadow each other.</summary>
    [Fact]
    public void TryClaim_DistinguishesCodeunit_AppGroup_AndBundle()
    {
        var erm = new UnitClaimQueue(_dir, "/b/erm");
        var scm = new UnitClaimQueue(_dir, "/b/scm");

        Assert.True(erm.TryClaim("App", "Codeunit50000"));
        Assert.True(erm.TryClaim("App", "Codeunit50001"));
        Assert.True(erm.TryClaim("OtherApp", "Codeunit50000"));
        Assert.True(scm.TryClaim("App", "Codeunit50000"));
    }

    [Fact]
    public void IsClaimed_ReadsWithoutClaiming()
    {
        var q = new UnitClaimQueue(_dir, "/b/erm");

        Assert.False(q.IsClaimed("App", "Codeunit1"));
        Assert.False(q.IsClaimed("App", "Codeunit1"));   // asking did not claim it
        Assert.True(q.TryClaim("App", "Codeunit1"));
        Assert.True(q.IsClaimed("App", "Codeunit1"));
    }

    /// <summary>The third state: a directory that cannot be written is not "someone else has it" —
    /// reading it as claimed would drop the unit from the run with no word said.</summary>
    [Fact]
    public void TryClaim_ThrowsWhenTheClaimDirectoryIsGone_RatherThanReportingAClaim()
    {
        var q = new UnitClaimQueue(Path.Combine(_dir, "no-such-dir"), "/b/erm");

        Assert.ThrowsAny<IOException>(() => q.TryClaim("App", "Codeunit1"));
    }

    [Fact]
    public void ForBundle_NoClaimDirectory_IsNull_TheOrdinaryRun()
    {
        Assert.Null(UnitClaimQueue.ForBundle("/b/erm", dir: null, list: null));
        Assert.Null(UnitClaimQueue.ForBundle("/b/erm", dir: "", list: "/b/erm"));
    }

    [Fact]
    public void ForBundle_OnlyAListedBundleIsClaimed()
    {
        Assert.NotNull(UnitClaimQueue.ForBundle("/b/erm", _dir, "/b/erm|/b/scm"));
        Assert.NotNull(UnitClaimQueue.ForBundle("/b/erm/", _dir, "/b/erm"));     // trailing separator
        Assert.Null(UnitClaimQueue.ForBundle("/b/other", _dir, "/b/erm|/b/scm"));
    }

    /// <summary>A directory with no bundle list is a broken hand-off. Null there would run every
    /// codeunit on every worker, which reads as a green run with each test counted per worker.</summary>
    [Fact]
    public void ForBundle_DirectoryWithoutBundleList_Refuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => UnitClaimQueue.ForBundle("/b/erm", _dir, null));
        Assert.Contains(UnitClaimQueue.BundlesEnvVar, ex.Message);
    }
}
