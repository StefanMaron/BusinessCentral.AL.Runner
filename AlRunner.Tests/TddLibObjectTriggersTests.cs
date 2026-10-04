// --tdd and the objects a library runs by id (#5322): `al-runner --tdd a b c`. Library `a` runs a report, a query and an
// xmlport by id (it depends on nothing, so it cannot name the ones of the bundle declared after it); bundle `b` declares
// those objects; test bundle `c` depends on both and calls the library. The only link from a test to a trigger of `b` is
// the call edge `a` leaves, which a bundle records only when KeyGraphProbe sees a form that can start something in a later
// bundle, and no other such form is in any of the three folders: without the by-id clause no bundle recorded one. The
// orders are every permutation of the three folders.
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in the corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibObjectTriggersTests
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibObjectTriggers");

    private static string[] Folders(string order) => order.Select(c => Path.Combine(Root, c.ToString())).ToArray();

    private static string Stub(string member) => $"TOT B Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private static void AssertAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(4, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Equal(new[] { Stub("MBReportPre") }, run.StubsOf("ReportOpenedByIdInALibrary_StartsTheReportsTriggersOfAnotherBundle"));
        Assert.Equal(new[] { Stub("MBQueryOpen") }, run.StubsOf("QueryOpenedByIdInALibrary_StartsTheQueriesTriggersOfAnotherBundle"));
        Assert.Equal(new[] { Stub("MBXmlPre") }, run.StubsOf("XmlPortRunByIdInALibrary_StartsTheXmlPortsTriggersOfAnotherBundle"));
        Assert.Empty(run.StubsOf("NoObject_IsNotAnnotated"));
    }

    /// <summary>Every test is annotated with exactly the stubs it reaches, whatever order the folders are listed in.</summary>
    [SkippableTheory]
    [InlineData("abc")]
    [InlineData("acb")]
    [InlineData("bac")]
    [InlineData("bca")]
    [InlineData("cab")]
    [InlineData("cba")]
    public void ObjectsRunByALibrary_AreFollowedInEveryListingOrder(string order)
    {
        TestArtifacts.SkipIfMissing();
        using var run = new TddRunResult(Folders(order));
        AssertAnnotated(run, order);
    }

    /// <summary>
    /// ONE cache, a plain run first (it caches the library with no edges, and refuses the missing stubs), then two --tdd
    /// runs, each of which must recompile the library to leave them, then a plain run that is never served a stub.
    /// </summary>
    [SkippableTheory]
    [InlineData("abc")]
    [InlineData("cba")]
    public void PlainThenTddThenTddThenPlainOnOneCache_AnnotatesEveryTddRunAndNeverServesAStubToAPlainOne(string order)
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-libobject-cache-" + order);
        Directory.CreateDirectory(cache);
        try
        {
            var folders = Folders(order);
            using var plain = new TddRunResult(folders) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, plain.Exit);
            using var first = new TddRunResult(folders) { CacheRoot = cache };
            AssertAnnotated(first, "tdd after a plain run, " + order);
            using var second = new TddRunResult(folders) { CacheRoot = cache };
            AssertAnnotated(second, "tdd after a tdd run, " + order);
            using var after = new TddRunResult(folders) { CacheRoot = cache, Plain = true };
            Assert.NotEqual(0, after.Exit);
            Assert.DoesNotContain(after.Tests, t => t.GetProperty("status").GetString() == "pass");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
