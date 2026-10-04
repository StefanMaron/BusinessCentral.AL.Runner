// A warm --tdd run answers like the cold run on the same sources (#5306). The compiled-deps cache
// is keyed on a source dependency's own package bytes, so a --tdd chain whose libraries are
// regenerated pass after pass must not be served a library compiled against a neighbour that
// had a member the run has not generated yet. CI provisions a fresh cache per leg, so it cannot
// see a warm one; a local run twice on one root can. Cold, warm and a third run on one root must
// agree on every line that says what the run generated, what it left uncompiled, and which test
// reached a generated stub.
//
// Runner-specific (--tdd and the runner's own cache), so it lives here, not in the al-language corpus.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddRerunLimitWarmCacheTests
{
    /// <summary>
    /// What a run says about itself, in the order it says it: exit code, each end-of-run
    /// line naming members (the limit line, the generated list, the closing block, the tests that
    /// reached a stub) and the status and stubs of every test.
    /// </summary>
    private static string[] Verdict(TddRunResult run)
    {
        var lines = new List<string> { $"exit {run.Exit}" };
        lines.AddRange(run.StdErr.Split('\n').Select(l => l.TrimEnd('\r')).Where(l =>
            l.StartsWith("--tdd: the re-run limit", StringComparison.Ordinal)
            || l.StartsWith("--tdd: generated ", StringComparison.Ordinal) && l.Contains("this run", StringComparison.Ordinal)
            || l.StartsWith("--tdd: ", StringComparison.Ordinal) && l.Contains("not compiled in", StringComparison.Ordinal)
            || l.StartsWith("--tdd: ", StringComparison.Ordinal) && l.Contains("reach generated stubs", StringComparison.Ordinal)
            || l.StartsWith("  Lib Chain ", StringComparison.Ordinal)));
        foreach (var t in run.Tests)
            lines.Add($"{t.GetProperty("name").GetString()}: {t.GetProperty("status").GetString()} " +
                      $"[{string.Join("; ", run.StubsOf(t.GetProperty("name").GetString()!))}]");
        return lines.ToArray();
    }

    private static void AssertAgree(string[] cold, TddRunResult run, string which)
        => Assert.True(cold.SequenceEqual(Verdict(run)),
            $"{which} run disagrees with the cold run.\ncold:\n{string.Join("\n", cold)}\n{which}:\n{string.Join("\n", Verdict(run))}\n{run.StdErr}");

    /// <summary>
    /// Cold, warm and a third run on one cache, the test listed first so the dependency load of the
    /// test bundle compiles lib1..lib3. The chain is one link past the limit, so the cold run
    /// reports it; a warm run must report exactly the same members and fail the same test. Before
    /// the fix the second or third run named another member, and sometimes exited 0.
    /// </summary>
    [SkippableFact]
    public void ChainAtTheLimit_WarmRunsAnswerLikeTheColdRun()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-warm-limit");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(LongChainFolders.FourLinkReversed()) { CacheRoot = cache };
            Assert.True(cold.Exit == 1, cold.StdErr);
            Assert.Equal("fail", cold.Find("Reaches").GetProperty("status").GetString());
            var limit = Assert.Single(cold.StdErr.Split('\n'), l => l.StartsWith("--tdd: the re-run limit", StringComparison.Ordinal));
            Assert.Contains("Lib Chain 3: procedure \"M4\"", limit);
            var coldVerdict = Verdict(cold);

            using var warm = new TddRunResult(LongChainFolders.FourLinkReversed()) { CacheRoot = cache };
            AssertAgree(coldVerdict, warm, "warm");
            using var third = new TddRunResult(LongChainFolders.FourLinkReversed()) { CacheRoot = cache };
            AssertAgree(coldVerdict, third, "third");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The control: a chain of exactly the limit generates every member and passes. A warm run must
    /// say the same, so the fix cannot be a refusal to serve the cache at all, or a fail-everything.
    /// </summary>
    [SkippableFact]
    public void ChainWithinTheLimit_WarmRunAnswersLikeTheColdRun()
    {
        TestArtifacts.SkipIfMissing();
        var cache = TestScratch.Dir("al-runner-tdd-warm-within-limit");
        Directory.CreateDirectory(cache);
        try
        {
            using var cold = new TddRunResult(LongChainFolders.AtTheLimit()) { CacheRoot = cache };
            Assert.True(cold.Exit == 0, cold.StdErr);
            Assert.Equal("pass", cold.Find("Reaches").GetProperty("status").GetString());
            Assert.Equal(new[] { "Lib Chain 2: procedure \"M3\"(Arg1: Integer): Integer" }, cold.StubsOf("Reaches"));
            var coldVerdict = Verdict(cold);

            using var warm = new TddRunResult(LongChainFolders.AtTheLimit()) { CacheRoot = cache };
            AssertAgree(coldVerdict, warm, "warm");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }
}
