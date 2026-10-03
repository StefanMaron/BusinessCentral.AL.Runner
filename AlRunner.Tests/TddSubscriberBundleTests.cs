// --tdd across source bundles where the event publisher is in a bundle compiled BEFORE the subscriber
// (#5264): `al-runner --tdd app lib test`, the app raising events whose subscribers sit in the library
// and in the test bundle, and call a member the app does not declare. The test calls the app's
// procedure that raises the event, so it reaches the generated stub through a subscriber that no call
// graph of one compile connects to it. Without the annotation the test passes against the stub silently.
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in
// the al-language corpus: the claim is about the generator, not about BC.
using Xunit;

namespace AlRunner.Tests;

/// <summary>The app, the library and the test bundle in dependency order, one run on a fresh cache.</summary>
public sealed class TddSubscriberBundleRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibBundleSubscriber");

    public static string[] ReversedFolders { get; } =
        { Path.Combine(Root, "test"), Path.Combine(Root, "lib"), Path.Combine(Root, "app") };

    public TddSubscriberBundleRun() : base(
        Path.Combine(Root, "app"), Path.Combine(Root, "lib"), Path.Combine(Root, "test")) { }
}

public sealed class TddSubscriberBundleTests : IClassFixture<TddSubscriberBundleRun>
{
    private readonly TddSubscriberBundleRun _run;

    public TddSubscriberBundleTests(TddSubscriberBundleRun run) => _run = run;

    private const string FromLib = "Lib Sub Publisher: procedure \"MissingFromLib\"(Arg1: Integer): Integer";
    private const string FromTest = "Lib Sub Publisher: procedure \"MissingFromTest\"(Arg1: Integer): Integer";
    private const string FromChain = "Lib Sub Publisher: procedure \"MissingFromChain\"(Arg1: Integer): Integer";
    private const string FromSecondRound = "Lib Sub Target: procedure \"MissingR\"(Arg1: Integer): Integer";
    private const string ViaHelper = "Lib Sub Publisher: procedure \"MissingViaHelper\"(Arg1: Integer): Integer";

    private static void AssertEveryTestPasses(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(7, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Contains("--tdd: generated 5 member(s) this run:", run.StdErr);
    }

    private static void AssertAnnotated(TddRunResult run, string which)
    {
        AssertEveryTestPasses(run, which);
        // The subscriber is in the library: the test raises the event by calling the app, directly and
        // through another procedure of the app that calls it.
        Assert.Equal(new[] { FromLib }, run.StubsOf("RaisesEventSubscribedInLib"));
        Assert.Equal(new[] { FromLib }, run.StubsOf("RaisesItThroughAnotherAppProcedure"));
        // The subscriber is in the test bundle itself, the publisher still in the app.
        Assert.Equal(new[] { FromTest }, run.StubsOf("RaisesEventSubscribedInTheTestBundle"));
        // app -> (library subscriber raises another event) -> (test bundle subscriber).
        Assert.Equal(new[] { FromChain }, run.StubsOf("RaisesAChainOfEventsAcrossThreeBundles"));
        // The test bundle's subscriber reaches the stub through a library procedure, which only
        // the library's compile knows reaches it.
        Assert.Equal(new[] { ViaHelper }, run.StubsOf("RaisesAnEventWhoseSubscriberCallsALibrary"));
        // Two rounds of subscribers, the member generated into the library's OWN compile (#5271): the
        // library's subscriber of OnY calls a procedure that raises OnX, whose subscriber reaches the stub.
        // No later pass of a compile that generated runs the search again, so the first round alone is silent.
        Assert.Equal(new[] { FromSecondRound }, run.StubsOf("RaisesAnEventWhoseSubscriberRaisesAnotherEventOfTheLibrary"));
        // The control: an event of the same publisher that nobody subscribes to.
        Assert.Empty(run.StubsOf("RaisesAnEventNobodySubscribesTo_IsNotAnnotated"));
    }

    /// <summary>
    /// #5264: the subscriber's compile generates the member into the app and names the tests of ITS
    /// compile; a test that raises the event by calling the app is reached through the app's own call
    /// edges, which an earlier compile of the run recorded.
    /// </summary>
    [SkippableFact]
    public void TestRaisingAnEventSubscribedInAnotherBundle_IsAnnotated()
    {
        TestArtifacts.SkipIfMissing();
        AssertAnnotated(_run, "app lib test");
        Assert.Contains("--tdd: 6 test(s) reach generated stubs this run:", _run.StdErr);
    }

    /// <summary>
    /// The test bundle first: the app and the library are compiled by its dependency load, and on the
    /// second run against ONE cache root the app is a compiled-deps hit that compiles nothing. Its call
    /// edges must still exist when the library's subscriber is followed, so a source bundle another
    /// bundle depends on is compiled again, and the warm run annotates as the cold one does.
    /// </summary>
    [SkippableFact]
    public void TestBundleListedFirst_AnnotatesOnAColdAndOnAWarmCache()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-subscriber-warm");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(TddSubscriberBundleRun.ReversedFolders) { CacheRoot = cache };
            AssertAnnotated(cold, "cold");
            using var warm = new TddRunResult(TddSubscriberBundleRun.ReversedFolders) { CacheRoot = cache };
            AssertAnnotated(warm, "warm");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
