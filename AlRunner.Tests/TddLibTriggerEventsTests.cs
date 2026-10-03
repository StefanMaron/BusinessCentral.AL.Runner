// --tdd and the paths into a stub that no call binds (#5286), across source bundles: `al-runner --tdd app lib
// test`. The app declares tables and the procedures that write to them; the library subscribes to their
// database events, extends a table with a trigger, declares a table with a trigger and a codeunit with an
// OnRun; the test bundle subscribes too and runs the operations. The operation that starts a trigger or an
// event is in a bundle compiled BEFORE the one holding it, so only the edges the earlier compile recorded
// connect a test calling it to a stub (the same edges #5264 records for event publishers).
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in the
// al-language corpus: the claim is about the generator, not about BC.
using Xunit;

namespace AlRunner.Tests;

/// <summary>The app, the library and the test bundle in dependency order, one run on a fresh cache.</summary>
public sealed class TddLibTriggerEventsRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibTriggerEvents");

    public static string[] ReversedFolders { get; } =
        { Path.Combine(Root, "test"), Path.Combine(Root, "lib"), Path.Combine(Root, "app") };

    public TddLibTriggerEventsRun() : base(
        Path.Combine(Root, "app"), Path.Combine(Root, "lib"), Path.Combine(Root, "test")) { }
}

public sealed class TddLibTriggerEventsTests : IClassFixture<TddLibTriggerEventsRun>
{
    private readonly TddLibTriggerEventsRun _run;

    public TddLibTriggerEventsTests(TddLibTriggerEventsRun run) => _run = run;

    private static string Stub(string member) => $"TLib Lib Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static string TestStub(string member) => $"TLib Test Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static void AssertStubs(TddRunResult run, string test, params string[] members)
    {
        Assert.Equal("pass", run.Find(test).GetProperty("status").GetString());
        // The test bundle's own members are named by their own codeunit.
        Assert.Equal(members.Select(m => m is "MissingTestDelete" or "MissingChain" ? TestStub(m) : Stub(m)).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            run.StubsOf(test).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Every assertion of the fixture, for a run in either listing order.</summary>
    internal static void AssertAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(13, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));

        // The trigger is in the library's table and its stub in the library's own compile, which the test
        // bundle's Insert(true) reaches (the key of the trigger is kept for the later compile).
        AssertStubs(run, "InsertTrue_RunsTheTriggerOfTheLibrarysTable", "MissingLibTrigger");
        // The subscriber is in the library: the test inserts itself, or calls the app procedure that does.
        AssertStubs(run, "Insert_RaisesTheEventTheLibrarySubscribesTo", "MissingLibEvent");
        AssertStubs(run, "AppProcedureInserting_RaisesTheEventTheLibrarySubscribesTo", "MissingLibEvent");
        // The trigger is the library's table extension's (it runs for the app's table); the setup insert of the
        // test raises the event the library subscribes to.
        AssertStubs(run, "ModifyTrue_RunsTheTriggerOfTheLibrarysTableExtension", "MissingLibEvent", "MissingExtTrigger");
        AssertStubs(run, "AppProcedureModifying_RunsTheTriggerOfTheLibrarysTableExtension", "MissingLibEvent", "MissingExtTrigger");
        // The subscriber is in the test bundle, the delete is the app's.
        AssertStubs(run, "AppProcedureDeleting_RaisesTheEventTheTestBundleSubscribesTo", "MissingLibEvent", "MissingTestDelete");
        // Codeunit.Run by name from the test bundle, and by id from the app, which names no codeunit.
        AssertStubs(run, "CodeunitRunByName_RunsTheLibrarysOnRun", "MissingLibRun");
        AssertStubs(run, "AppProcedureRunningById_RunsTheLibrarysOnRun", "MissingLibRun");
        // A RecordRef names no table: the app's insert by table id starts the trigger of the library's table,
        // and every other subscriber of an insert, and no other operation's.
        var viaApp = run.StubsOf("AppProcedureInsertingThroughARecordRef_StartsTheTriggerOfALaterBundlesTable");
        // The RecordRef is in the test bundle itself, the trigger in the library compiled before it.
        var direct = run.StubsOf("RecordRefInsertInTheTestBundle_StartsTheTriggerOfTheLibrarysTable");
        foreach (var stubs in new[] { viaApp, direct })
        {
            foreach (var member in new[] { "MissingLibTrigger", "MissingLibEvent", "MissingChain" })
                Assert.Contains(member == "MissingChain" ? TestStub(member) : Stub(member), stubs);
            foreach (var member in new[] { "MissingExtTrigger", "MissingLibRun" })
                Assert.DoesNotContain(Stub(member), stubs);
            Assert.DoesNotContain(TestStub("MissingTestDelete"), stubs);
        }
        // Subscribers in the test bundle, the second started by a procedure of the first (a second round).
        AssertStubs(run, "InsertIntoTheChainTable_NeedsASecondRound", "MissingChain");
        // The controls.
        AssertStubs(run, "AppProcedureWritingToAQuietTable_IsNotAnnotated");
        AssertStubs(run, "Quiet_IsNotAnnotated");
    }

    /// <summary>
    /// #5286: the app's compile leaves the edges of its operations (an insert, a modify with RunTrigger, a
    /// delete, a RecordRef insert, a Codeunit.Run by id), and the later compiles read them when their
    /// subscribers, triggers and OnRun reach a stub.
    /// </summary>
    [SkippableFact]
    public void OperationInAnEarlierBundle_ReachesTheTriggerAndSubscriberOfALaterOne()
    {
        TestArtifacts.SkipIfMissing();
        AssertAnnotated(_run, "app lib test");
        Assert.Contains("--tdd: generated 6 member(s) this run:", _run.StdErr);
    }

    /// <summary>
    /// The test bundle first: the app and the library are compiled by its dependency load, and on the second
    /// run against ONE cache root the app is a compiled-deps hit that compiles nothing, so it is compiled again
    /// to leave its edges (a source dependency with none is never served from the cache under --tdd).
    /// </summary>
    [SkippableFact]
    public void TestBundleListedFirst_AnnotatesOnAColdAndOnAWarmCache()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-trigger-warm");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(TddLibTriggerEventsRun.ReversedFolders) { CacheRoot = cache };
            AssertAnnotated(cold, "cold");
            using var warm = new TddRunResult(TddLibTriggerEventsRun.ReversedFolders) { CacheRoot = cache };
            AssertAnnotated(warm, "warm");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
