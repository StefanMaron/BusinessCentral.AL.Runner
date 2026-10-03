// --tdd over a test library of TWO objects, listed after the test bundle, against ONE cache root
// (#5243, #5263, #5161). With one object the compile against the app's old symbols leaves the library
// empty; with two it leaves it partial, and a partial result is what the compiled-deps cache can keep.
// Cold and warm differ only here, and CI provisions a fresh cache per leg, so it cannot see the warm one.
//
// Runner-specific (--tdd and the runner's own cache), so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddLibBundleWarmCacheTests
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibBundlePartial");

    // The test bundle first: the libraries are then compiled by its dependency load, not by their own
    // bundle iterations.
    private static readonly string[] Folders =
        { Path.Combine(Root, "test"), Path.Combine(Root, "lib"), Path.Combine(Root, "app") };

    private const string Stub = "Lib Partial Loyalty: procedure \"Missing\"(Arg1: Integer): Integer";

    private static string[] PartialLibraryReports(string cache)
        => Directory.Exists(cache)
            ? Directory.GetFiles(cache, "*.emit-excluded.txt", SearchOption.AllDirectories)
            : Array.Empty<string>();

    private static void AssertGeneratedAndAnnotated(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 0, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.DoesNotContain("EMIT-ZERO", run.StdErr);
        Assert.Equal(3, run.Tests.Count);
        Assert.All(run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Contains("--tdd: generated 1 member(s) this run:", run.StdErr);
        Assert.Equal(new[] { Stub }, run.StubsOf("ViaLibrary_RunsAgainstTheGeneratedStub"));
        // Reached through a procedure of the test codeunit that calls the library, not in the test's body.
        Assert.Equal(new[] { Stub }, run.StubsOf("ThroughLocalHelper_RunsAgainstTheGeneratedStub"));
        // The library's other object touches nothing missing.
        Assert.Empty(run.StubsOf("OtherLibraryObject_IsNotAnnotated"));
    }

    /// <summary>
    /// The compile against the app's old symbols drops one of the library's two objects and keeps the
    /// other. That partial result must not be cached by a --tdd pass that is about to re-run: the
    /// second run on the same cache would be served it, compile nothing, and fail where it reaches the
    /// dropped object. Cold and warm both generate and annotate, and the cache holds no partial library.
    /// </summary>
    [SkippableFact]
    public void TwoObjectLibraryListedAfterTheTests_GeneratesOnAColdAndOnAWarmCache()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-lib-partial-warm");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(Folders) { CacheRoot = cache };
            AssertGeneratedAndAnnotated(cold, "cold");
            Assert.Empty(PartialLibraryReports(cache));

            using var warm = new TddRunResult(Folders) { CacheRoot = cache };
            AssertGeneratedAndAnnotated(warm, "warm");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// #5263: a plain run drops the same object, reports it, and caches the library with one of its two
    /// objects. A --tdd run on that cache must not be served it (the key has no --tdd term): it compiles
    /// the library, sees the missing member, and generates it.
    /// </summary>
    [SkippableFact]
    public void PlainRunThenTddRunOnOneCache_DoesNotServeThePartialLibraryToTdd()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-lib-partial-plain");
        Directory.CreateDirectory(cache);
        try
        {
            using var plain = new TddRunResult(Folders) { CacheRoot = cache, Plain = true };
            Assert.True(plain.Exit != 0, $"the plain run should not pass\n{plain.StdErr}");
            // The precondition: the plain run did cache a partial library naming the missing member.
            var reports = PartialLibraryReports(cache);
            Assert.Single(reports);
            Assert.Contains("AL0132", File.ReadAllText(reports[0]));

            using var tdd = new TddRunResult(Folders) { CacheRoot = cache };
            AssertGeneratedAndAnnotated(tdd, "tdd after plain");
            Assert.DoesNotContain("no test referenced a missing symbol", tdd.StdErr);
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
