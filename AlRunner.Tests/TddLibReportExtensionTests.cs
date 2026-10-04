// --tdd and a report extension of a library's report (#5322): `al-runner --tdd a b c`. Library `a` declares a report and
// runs it by name; bundle `b` extends it with a report extension whose OnPreReport calls a stub; test bundle `c` depends
// on both and calls the library. Nothing in `a` names `b`'s code, so the only link from the test to the extension's trigger
// is the call edge `a` leaves, which a bundle records only when KeyGraphProbe sees a form that can start something in a
// later bundle: the report extension is the only such form in the three folders, and without its clause no bundle
// recorded one. The orders are every permutation of the three folders.
//
// Runner-specific (the --tdd call graph across the runner's own compiles), so it lives here, not in the corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibReportExtensionTests
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibReportExtension");

    private static string[] Folders(string order) => order.Select(c => Path.Combine(Root, c.ToString())).ToArray();

    private static void AssertAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(2, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Equal(new[] { "TRE B Target: procedure \"MBExtPre\"(Arg1: Integer): Integer" },
            run.StubsOf("ReportOfALibrary_ExtendedByAnotherBundle_StartsTheExtensionsTrigger"));
        Assert.Empty(run.StubsOf("NoReport_IsNotAnnotated"));
    }

    /// <summary>The test is annotated whatever order the folders are listed in.</summary>
    [SkippableTheory]
    [InlineData("abc")]
    [InlineData("acb")]
    [InlineData("bac")]
    [InlineData("bca")]
    [InlineData("cab")]
    [InlineData("cba")]
    public void AReportOfALibraryExtendedByALaterBundle_IsFollowedInEveryListingOrder(string order)
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
        var cache = TestScratch.Dir("al-runner-tdd-libreportext-cache-" + order);
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
