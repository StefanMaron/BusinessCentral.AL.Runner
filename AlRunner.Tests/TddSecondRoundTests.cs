// --tdd, a member generated into a library's OWN compile (#5271) and reached through three rounds of event
// subscribers (#5264): `al-runner --tdd app lib test`, where the library's subscriber of "OnZ" calls a
// procedure that raises "OnY", whose subscriber calls one that raises "OnX", whose subscriber calls "MissingR"
// on a codeunit of the library. No bundle is recompiled and no second pass runs; the compile follows its own
// reaching procedures once more after generating, which finds one further round, so three rounds are what only
// the repeat-until-nothing-new search of TddCallGraph.ReachClosure finds: stopping it early leaves the test
// passing against the stub with no annotation.
//
// Runner-specific, so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddSecondRoundRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibSecondRound");

    public static string[] ReversedFolders { get; } =
        { Path.Combine(Root, "test"), Path.Combine(Root, "lib"), Path.Combine(Root, "app") };

    public TddSecondRoundRun() : base(
        Path.Combine(Root, "app"), Path.Combine(Root, "lib"), Path.Combine(Root, "test")) { }
}

public sealed class TddSecondRoundTests : IClassFixture<TddSecondRoundRun>
{
    private readonly TddSecondRoundRun _run;

    public TddSecondRoundTests(TddSecondRoundRun run) => _run = run;

    private const string Stub = "Round Target: procedure \"MissingR\"(Arg1: Integer): Integer";
    private const string AppStub = "Round Publisher: procedure \"MissingApp\"(Arg1: Integer): Integer";

    private static void AssertAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(3, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Contains("--tdd: generated 2 member(s) this run:", run.StdErr);
        Assert.Equal(new[] { Stub }, run.StubsOf("RaiseZ_ReachesTheStubThroughThreeRoundsOfSubscribers"));
        // The member on the app's codeunit, three rounds of subscribers in the test bundle: the compile that
        // generates it is the test bundle's, which does not follow its reaching procedures again, and the pass
        // after the recompile finds only two rounds by itself.
        Assert.Equal(new[] { AppStub }, run.StubsOf("RaiseZ2_ReachesTheStubInTheAppThroughThreeRoundsInTheTestBundle"));
        Assert.Empty(run.StubsOf("Quiet_IsNotAnnotated"));
    }

    [SkippableFact]
    public void SubscriberThatRaisesAnotherEvent_IsFollowedThroughEveryRound()
    {
        TestArtifacts.SkipIfMissing();
        AssertAnnotated(_run, "app lib test");
    }

    [SkippableFact]
    public void TestBundleListedFirst_FollowsEveryRoundToo()
    {
        TestArtifacts.SkipIfMissing();
        using var reversed = new TddRunResult(TddSecondRoundRun.ReversedFolders);
        AssertAnnotated(reversed, "test lib app");
    }
}
