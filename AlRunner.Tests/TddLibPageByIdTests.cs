// --tdd and a page opened by id in a library (#5309): `al-runner --tdd a b c`. Library `a` calls Page.Run(<id>)
// and depends on nothing; bundle `b` declares the page, whose OnOpenPage calls a stub; test bundle `c` depends on
// both and drives a TestPage the library opens. Nothing in `a` names the page, so the only link from the test to
// the page's trigger is the call edge `a` leaves, which a bundle records only when KeyGraphProbe sees a form that
// can start something in a later bundle. Without the Page.Run clause no bundle recorded one and the test passed
// through the stub with no annotation in most listing orders. The orders are every permutation of the three folders.
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in the corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibPageByIdTests
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibPageById");

    private static string[] Folders(string order) => order.Select(c => Path.Combine(Root, c.ToString())).ToArray();

    private static readonly string Stub = "TPI B Target: procedure \"MIdOpen\"(Arg1: Integer): Integer";

    private static void AssertAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(2, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Equal(new[] { Stub }, run.StubsOf("PageOpenedByIdInALibrary_StartsThePagesOnOpenPageOfAnotherBundle"));
        Assert.Empty(run.StubsOf("NoPage_IsNotAnnotated"));
    }

    /// <summary>The test is annotated whatever order the folders are listed in.</summary>
    [SkippableTheory]
    [InlineData("abc")]
    [InlineData("acb")]
    [InlineData("bac")]
    [InlineData("bca")]
    [InlineData("cab")]
    [InlineData("cba")]
    public void APageOpenedByIdInALibrary_IsFollowedInEveryListingOrder(string order)
    {
        TestArtifacts.SkipIfMissing();
        using var run = new TddRunResult(Folders(order));
        AssertAnnotated(run, order);
    }

    /// <summary>
    /// ONE cache, a plain run first (it caches the library with no edges, and refuses the missing stub), then two --tdd
    /// runs, each of which must recompile the library to leave them, then a plain run that is never served a stub.
    /// </summary>
    [SkippableTheory]
    [InlineData("abc")]
    [InlineData("cba")]
    public void PlainThenTddThenTddThenPlainOnOneCache_AnnotatesEveryTddRunAndNeverServesAStubToAPlainOne(string order)
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-pagebyid-cache-" + order);
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
