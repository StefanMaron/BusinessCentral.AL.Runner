// JobsSharedBundleAbortTests — what a watchdog abort reports when the bundle is SHARED between
// `--jobs` workers (#5215, on #5130). In a shared bundle a later codeunit another worker already
// claimed is not lost to this abort, and whether the abort is worth a resume
// (AbortResumePlan.AbandonedLaterCodeunits, which reads that count) depends on it.
//
// In-process on purpose: how many codeunits are still unclaimed when a worker hangs depends on
// how far the other worker has got, so a spawned run cannot pin it. Here the "other worker" is a
// second UnitClaimQueue on the same directory. The spawned half is JobsSharedBundleEndToEndTests.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSharedBundleAbortTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("sharedabort-");

    public JobsSharedBundleAbortTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class TestAttribute : Attribute { }

    // The codeunit that hangs: its first [Test] hangs, two more never run.
    public sealed class Codeunit200 { [Test] public void Hangs() { } [Test] public void Next1() { } [Test] public void Next2() { } }
    public sealed class Codeunit201 { [Test] public void A() { } [Test] public void B() { } }
    public sealed class Codeunit202 { [Test] public void A() { } }
    public sealed class Codeunit203 { [Test] public void A() { } [Test] public void B() { } [Test] public void C() { } }

    private static readonly Type Hung = typeof(Codeunit200);
    private static readonly Type[] AllTypes =
        { typeof(Codeunit200), typeof(Codeunit203), typeof(Codeunit201), typeof(Codeunit202) };

    private static string AssemblyName => Hung.Assembly.GetName().Name!;

    private static string Abort(UnitClaimQueue? claim)
    {
        var executor = new TestExecutor { UnitClaim = claim };
        var methods = Hung.GetMethods(System.Reflection.BindingFlags.Public
                                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .OrderBy(m => m.Name == "Hangs" ? 0 : 1).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray();
        var hungIndex = Array.FindIndex(methods, m => m.Name == "Hangs");

        executor.RecordAbortedSuite(Hung, methods[hungIndex], "Hung Codeunit", methods, hungIndex,
            AllTypes, 0, null, null);

        return Assert.Single(executor.AbortReasons);
    }

    private UnitClaimQueue Worker() => new(_dir, "/b/shared");

    private void OtherWorkerClaims(params Type[] types)
    {
        var other = Worker();
        foreach (var t in types) Assert.True(other.TryClaim(AssemblyName, t.Name), $"{t.Name} was already claimed");
    }

    /// <summary>With no claim queue every later codeunit is lost to the abort: the shape before
    /// sharing existed, and what a non-shared bundle still reports.</summary>
    [Fact]
    public void WithoutAClaimQueue_EveryLaterCodeunitIsCountedAsLost()
    {
        var reason = Abort(null);

        Assert.Contains("2 further [Test] method(s) in this codeunit and 6 in 3 subsequent codeunit(s)", reason);
        Assert.Contains("(8 total)", reason);
        Assert.DoesNotContain("claimed yet", reason);
        Assert.True(AbortResumePlan.AbandonedLaterCodeunits(reason));
    }

    /// <summary>The claim queue is what this is about: Codeunit203 (3 tests) belongs to another
    /// worker, so only Codeunit201 and Codeunit202 (3 tests) are still unclaimed and still lost.</summary>
    [Fact]
    public void ACodeunitAnotherWorkerClaimed_IsNotCountedAsLost()
    {
        OtherWorkerClaims(typeof(Codeunit203));

        var reason = Abort(Worker());

        Assert.Contains("2 further [Test] method(s) in this codeunit and 3 in 2 subsequent codeunit(s)", reason);
        Assert.Contains("(5 total)", reason);
        Assert.Contains("none of those codeunits was claimed yet", reason);
        Assert.True(AbortResumePlan.AbandonedLaterCodeunits(reason));
    }

    /// <summary>Every later codeunit is somebody else's: this abort took nothing else down, so it
    /// names no later codeunits and a resume is not worth a BC boot. The wording about
    /// "those codeunits" would describe nothing, so it is absent too.</summary>
    [Fact]
    public void WhenEveryLaterCodeunitIsClaimed_TheAbortAbandonsNoLaterCodeunit_AndNothingResumes()
    {
        OtherWorkerClaims(typeof(Codeunit201), typeof(Codeunit202), typeof(Codeunit203));

        var reason = Abort(Worker());

        Assert.Contains("2 further [Test] method(s) in this codeunit did not run (2 total)", reason);
        Assert.DoesNotContain("subsequent codeunit", reason);
        Assert.DoesNotContain("none of those codeunits", reason);
        Assert.False(AbortResumePlan.AbandonedLaterCodeunits(reason));
        Assert.False(AbortResumePlan.MakesProgress(new[] { reason }, Array.Empty<string>()));
    }

    /// <summary>The smallest case that still names later codeunits: exactly ONE is unclaimed. The
    /// clause and the "none of those codeunits" wording both belong to it, and a resume is worth it.</summary>
    [Fact]
    public void WithExactlyOneUnclaimedLaterCodeunit_TheAbortNamesIt_AndSaysNoneOfThemWasClaimed()
    {
        OtherWorkerClaims(typeof(Codeunit201), typeof(Codeunit203));

        var reason = Abort(Worker());

        Assert.Contains("and 1 in 1 subsequent codeunit(s)", reason);
        Assert.Contains("none of those codeunits was claimed yet", reason);
        Assert.True(AbortResumePlan.AbandonedLaterCodeunits(reason));
    }

    /// <summary>The count is read at the moment of the abort and reflects the claims then: a
    /// codeunit claimed after it does not change what it said, and one claimed before it does.
    /// Asking twice with a claim made between them gives two different answers.</summary>
    [Fact]
    public void TheCountFollowsTheClaimsAtTheTimeOfTheAbort()
    {
        var first = Abort(Worker());
        OtherWorkerClaims(typeof(Codeunit201), typeof(Codeunit202));
        var second = Abort(Worker());

        Assert.Contains("and 6 in 3 subsequent codeunit(s)", first);
        Assert.Contains("and 3 in 1 subsequent codeunit(s)", second);
    }
}
