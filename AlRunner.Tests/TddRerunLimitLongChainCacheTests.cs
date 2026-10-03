// The compiled-deps cache and the --tdd re-run limit reached in a dependency load (#5287, #5243, #5271).
// At the limit the load of a library goes on instead of throwing, and what it compiled against a bundle
// still waiting for its recompile is not a result to keep: its key does not name that bundle. The fixture
// has two sibling libraries calling the same missing member, so one compile generates it and the other
// (partial: it has a second object that compiles) finds it already generated and generates nothing itself.
// CI provisions a fresh cache per leg, so it cannot see a warm one.
//
// Runner-specific (--tdd and the runner's own cache), so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddRerunLimitLongChainCacheTests
{
    private static string[] PartialLibraryReports(string cache)
        => Directory.Exists(cache)
            ? Directory.GetFiles(cache, "*.emit-excluded.txt", SearchOption.AllDirectories)
            : Array.Empty<string>();

    /// <summary>
    /// The test bundle listed first, so its dependency load compiles lib4, which generates the last member
    /// into lib3, and then lib4b, which drops one of its two objects for the same member and generates
    /// nothing. That is a stale compile at the limit: the run reports the limit and finishes, and the cache
    /// keeps no partial library. A second run on the same cache finishes the same way (which members it
    /// names differs: it is served the libraries the first run compiled complete). A plain run on the cache
    /// stops at lib1, whose only object calls a member the app lacks, so it cannot reach lib4b.
    /// </summary>
    [SkippableFact]
    public void TddRunPastTheLimitTwiceOnOneCache_DoesNotCacheThePartialLibraryOfTheStalePass()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-long-chain-cache");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(LongChainFolders.ReversedWithTwoFourthLinks()) { CacheRoot = cache };
            Assert.True(cold.Exit == 1, cold.StdErr);
            Assert.DoesNotContain("FATAL", cold.StdErr);
            // The precondition: the compile of the library did lose an object in the stale pass.
            Assert.Contains("1 of this dependency's 2 object(s) could not be compiled", cold.StdErr);
            Assert.Contains("--tdd: the re-run limit (3) was reached with 2 member(s)", cold.StdErr);
            Assert.Empty(PartialLibraryReports(cache));

            using var warm = new TddRunResult(LongChainFolders.ReversedWithTwoFourthLinks()) { CacheRoot = cache };
            Assert.True(warm.Exit == 1, warm.StdErr);
            Assert.DoesNotContain("FATAL", warm.StdErr);
            Assert.Contains("--tdd: the re-run limit (3) was reached", warm.StdErr);
            Assert.Empty(PartialLibraryReports(cache));
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
