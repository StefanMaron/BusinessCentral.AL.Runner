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

    private static TddExcludedObjectDetail Dropped(string file, string name)
        => new(file, name, Array.Empty<string>());

    /// <summary>#5256: every worker of a shared bundle finds the same dropped objects. Between two
    /// of them each object goes to exactly one, so the tests of the two workers sum to the
    /// bundle's, and two objects dropped together may fall to different workers.</summary>
    [Fact]
    public void ClaimDropped_GivesEachDroppedObjectToExactlyOneWorker()
    {
        var details = new[] { Dropped("/b/erm/A.al", "A"), Dropped("/b/erm/B.al", "B") };
        var worker1 = new UnitClaimQueue(_dir, "/b/erm");
        var worker2 = new UnitClaimQueue(_dir, "/b/erm");

        var first = worker1.ClaimDropped("App", details);
        var second = worker2.ClaimDropped("App", details);

        Assert.Equal(new[] { "A", "B" }, first.Select(d => d.ObjectDisplayName));
        Assert.Empty(second);

        // one object already taken by the other worker: only the free one is claimed
        var third = new UnitClaimQueue(_dir, "/b/scm");
        Assert.True(third.TryClaimDropped("App", "/b/scm/A.al|A"));
        Assert.Equal(new[] { "B" }, third.ClaimDropped("App", new[]
            { Dropped("/b/scm/A.al", "A"), Dropped("/b/scm/B.al", "B") }).Select(d => d.ObjectDisplayName));
    }

    /// <summary>A dropped object's claim must not shadow, or be shadowed by, a real codeunit's: the
    /// dropped object never reaches the run, but a codeunit of the same name in another app group
    /// does, and losing that claim would drop a test codeunit's results.</summary>
    [Fact]
    public void ClaimDropped_DoesNotCollideWithARealCodeunitsClaim_OrWithAnotherBundle()
    {
        var q = new UnitClaimQueue(_dir, "/b/erm");

        // a codeunit type name spelled exactly as the dropped object's key would be, were it unprefixed
        Assert.True(q.TryClaim("App", "A|A"));
        Assert.Single(q.ClaimDropped("App", new[] { Dropped("A", "A") }));
        Assert.Single(q.ClaimDropped("OtherApp", new[] { Dropped("A", "A") }));
        Assert.Single(new UnitClaimQueue(_dir, "/b/scm").ClaimDropped("App", new[] { Dropped("A", "A") }));
        Assert.Empty(q.ClaimDropped("App", new[] { Dropped("A", "A") }));
    }

    /// <summary>#5262: a --tdd re-run compiles a worker's bundle again and finds its own claim files, which
    /// answer "exists". The set of what this process won makes the second pass keep them; a claim a PEER holds
    /// is still lost with the same set, and the set is keyed by bundle and app group so one bundle's win says
    /// nothing about a same-named object in another.</summary>
    [Fact]
    public void ClaimDropped_WithAnOwnedSet_KeepsWhatThisProcessWon_AndStillLosesWhatAPeerWon()
    {
        var details = new[] { Dropped("/b/erm/A.al", "A"), Dropped("/b/erm/B.al", "B") };
        var owned = new HashSet<string>();

        Assert.Equal(new[] { "A", "B" }, new UnitClaimQueue(_dir, "/b/erm").ClaimDropped("App", details, owned).Select(d => d.ObjectDisplayName));
        // the second pass is another queue over the same directory, as ForBundle builds one per call
        Assert.Equal(new[] { "A", "B" }, new UnitClaimQueue(_dir, "/b/erm").ClaimDropped("App", details, owned).Select(d => d.ObjectDisplayName));
        // the control: the files exist, so without the set the same second pass would own nothing
        Assert.Empty(new UnitClaimQueue(_dir, "/b/erm").ClaimDropped("App", details));
        // a peer process has its own (empty) set, and loses
        Assert.Empty(new UnitClaimQueue(_dir, "/b/erm").ClaimDropped("App", details, new HashSet<string>()));

        // what a peer won stays the peer's, though this process owns the same name elsewhere
        Assert.True(new UnitClaimQueue(_dir, "/b/scm").TryClaimDropped("App", "/b/erm/A.al|A"));
        Assert.Empty(new UnitClaimQueue(_dir, "/b/scm").ClaimDropped("App", new[] { details[0] }, owned));
        Assert.True(new UnitClaimQueue(_dir, "/b/erm").TryClaimDropped("OtherApp", "/b/erm/A.al|A"));
        Assert.Empty(new UnitClaimQueue(_dir, "/b/erm").ClaimDropped("OtherApp", new[] { details[0] }, owned));
    }

    /// <summary>#5326: --tdd's re-run throws a pass's results away, so the claims that pass made on test codeunits
    /// must be free again. The ledger gives back exactly what THIS process created: the re-run claims each once,
    /// and a peer's claim, which the ledger never saw, is still taken.</summary>
    [Fact]
    public void Release_GivesBackWhatThisProcessClaimed_AndNothingAPeerHolds()
    {
        var ledger = new UnitClaimLedger();
        var peer = new UnitClaimQueue(_dir, "/b/erm");
        Assert.True(peer.TryClaim("App", "Peer"));

        Assert.True(new UnitClaimQueue(_dir, "/b/erm", ledger).TryClaim("App", "Mine1"));
        Assert.True(new UnitClaimQueue(_dir, "/b/erm", ledger).TryClaim("App", "Mine2"));
        Assert.False(new UnitClaimQueue(_dir, "/b/erm", ledger).TryClaim("App", "Peer"));

        Assert.Equal(2, ledger.Release());

        // the second pass: its own claims are free, claimed once each, the peer's still is not
        var second = new UnitClaimQueue(_dir, "/b/erm", ledger);
        Assert.True(second.TryClaim("App", "Mine1"));
        Assert.True(second.TryClaim("App", "Mine2"));
        Assert.False(second.TryClaim("App", "Mine1"));
        Assert.False(second.TryClaim("App", "Peer"));
        Assert.True(peer.IsClaimed("App", "Peer"));
    }

    /// <summary>A release gives back one pass's claims, not every pass's: the claims of the pass that kept its
    /// results stay taken, and a second release (a second re-run) gives back only what the pass in between made.</summary>
    [Fact]
    public void Release_EmptiesTheLedger_SoALaterReleaseGivesBackOnlyTheLaterClaims()
    {
        var ledger = new UnitClaimLedger();
        var q = new UnitClaimQueue(_dir, "/b/erm", ledger);
        Assert.True(q.TryClaim("App", "A"));
        Assert.Equal(1, ledger.Release());
        Assert.Equal(0, ledger.Release());
        Assert.False(q.IsClaimed("App", "A"));

        Assert.True(q.TryClaim("App", "A"));
        Assert.True(q.TryClaim("App", "B"));
        // nothing releases the final pass: both stay taken
        Assert.False(new UnitClaimQueue(_dir, "/b/erm").TryClaim("App", "A"));
        Assert.False(new UnitClaimQueue(_dir, "/b/erm").TryClaim("App", "B"));
    }

    /// <summary>A dropped object's claim is kept across the re-run by its owner (#5262), so it is not the
    /// ledger's: releasing it would let a peer claim the object and report its rows a second time.</summary>
    [Fact]
    public void Release_LeavesADroppedObjectsClaimWithItsOwner()
    {
        var ledger = new UnitClaimLedger();
        var owned = new HashSet<string>();
        var details = new[] { Dropped("/b/erm/A.al", "A") };
        var q = new UnitClaimQueue(_dir, "/b/erm", ledger);
        Assert.Single(q.ClaimDropped("App", details, owned));
        Assert.True(q.TryClaim("App", "Codeunit1"));

        Assert.Equal(1, ledger.Release());

        Assert.Empty(new UnitClaimQueue(_dir, "/b/erm").ClaimDropped("App", details));
        Assert.Single(q.ClaimDropped("App", details, owned));
    }

    /// <summary>The third state: a claim file that cannot be deleted is refused loudly, because leaving it would
    /// make the re-run skip a codeunit nobody reports.</summary>
    [Fact]
    public void Release_ThatCannotDeleteAClaimFile_Throws()
    {
        var ledger = new UnitClaimLedger();
        var blocker = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(blocker);
        File.WriteAllText(Path.Combine(blocker, "x"), "");
        ledger.Won(blocker);   // a directory with content: File.Delete refuses it

        var ex = Assert.Throws<InvalidOperationException>(() => ledger.Release());
        Assert.Contains("could not release the claim file", ex.Message);
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
