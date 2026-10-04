// --tdd's bounded re-run (#5265) when the last pass's generation happens in a DEPENDENCY LOAD (#5287).
// A chain of source bundles one link longer than TddLibChain (app, lib1..lib4, test) generates its last
// member into lib3 while lib4 is compiled, and the test bundle's load of lib4 then finds the batch still
// waiting for its recompile. Below the limit that is a stale pass and the cycle re-runs; at the limit no
// pass follows, and the run must report the limit, finish, and exit 1, not stop at a dependency-load FATAL.
//
// Runner-specific (--tdd's own bound), so it lives here, not in the al-language corpus. Each run is five
// or six bundles over four passes, so every run of this class is shared by the tests that read it.
using Xunit;

namespace AlRunner.Tests;

internal static class LongChainFolders
{
    private static readonly string Fixtures = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures");

    private static string Chain(string name) => Path.Combine(Fixtures, "TddLibChain", name);
    private static string Long(string name) => Path.Combine(Fixtures, "TddLibChainLong", name);

    /// <summary>app, lib1..lib4, test: each library calls a member the one before it lacks, and the test calls "M5".</summary>
    internal static string[] InOrder() => new[]
        { Chain("app"), Chain("lib1"), Chain("lib2"), Chain("lib3"), Long("lib4"), Long("test") };

    /// <summary>The same bundles with the test first and the app last: the test's dependency load compiles the libraries.</summary>
    internal static string[] Reversed() => InOrder().Reverse().ToArray();

    /// <summary>The test first, then lib4b, lib4, lib3.. app: the test's dependency load compiles lib4 (which generates the
    /// last member into lib3) and then lib4b (partial: one object calls the member, and this compile has nothing left to generate).</summary>
    internal static string[] ReversedWithTwoFourthLinks() => new[]
        { Long("testBoth"), Long("lib4b"), Long("lib4"), Chain("lib3"), Chain("lib2"), Chain("lib1"), Chain("app") };

    /// <summary>The #5265 chain, the test first: app lib1..lib3 and a test calling "M4", one link past the limit.</summary>
    internal static string[] FourLinkReversed() => new[]
        { Chain("test"), Chain("lib3"), Chain("lib2"), Chain("lib1"), Chain("app") };

    /// <summary>A chain of exactly the limit: app, lib1, lib2, test calling "M3": three re-runs and nothing left over.
    /// The test is listed first, so its dependency load compiles lib1 and lib2 and is stale until the third re-run.</summary>
    internal static string[] AtTheLimit() => new[]
        { Long("testAtLimit"), Chain("lib2"), Chain("lib1"), Chain("app") };
}

public sealed class LongChainInOrderRun : TddRunResult
{
    public LongChainInOrderRun() : base(LongChainFolders.InOrder()) { }
}

public sealed class LongChainReversedRun : TddRunResult
{
    public LongChainReversedRun() : base(LongChainFolders.Reversed()) { }
}

public sealed class LongChainAtTheLimitRun : TddRunResult
{
    public LongChainAtTheLimitRun() : base(LongChainFolders.AtTheLimit()) { }
}

public sealed class TddRerunLimitLongChainTests
    : IClassFixture<LongChainInOrderRun>, IClassFixture<LongChainReversedRun>, IClassFixture<LongChainAtTheLimitRun>
{
    private readonly LongChainInOrderRun _inOrder;
    private readonly LongChainReversedRun _reversed;
    private readonly LongChainAtTheLimitRun _atTheLimit;

    public TddRerunLimitLongChainTests(
        LongChainInOrderRun inOrder, LongChainReversedRun reversed, LongChainAtTheLimitRun atTheLimit)
    {
        _inOrder = inOrder;
        _reversed = reversed;
        _atTheLimit = atTheLimit;
    }

    private const string M4 = "Lib Chain 3: procedure \"M4\"(Arg1: Integer): Integer";
    private const string M5 = "Lib Chain 4: procedure \"M5\"(Arg1: Integer): Integer";

    /// <summary>
    /// The run finishes: the test that needs the member nobody compiled in fails (it never compiled), the
    /// JSON is written, and the exit is 1. Two lines say what the limit stopped: the one when it was
    /// reached and the closing block, naming the same two members of the last pass (the one lib4's compile
    /// generated into lib3, and the one the test's compile generated into lib4), and the list of generated
    /// members holds only the three that were compiled in.
    /// </summary>
    private static void AssertReportsTheLimit(TddRunResult run, string which)
    {
        Assert.True(run.Exit == 1, $"{which}: exit {run.Exit}\n{run.StdErr}");
        Assert.DoesNotContain("FATAL", run.StdErr);
        Assert.DoesNotContain("dep-load-fail", run.StdErr);
        Assert.Equal("fail", run.Find("Reaches").GetProperty("status").GetString());
        Assert.Contains("did not compile", run.Failure("Reaches"));

        var lines = run.StdErr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var limit = Assert.Single(lines, l => l.StartsWith("--tdd: the re-run limit (3) was reached", StringComparison.Ordinal));
        Assert.StartsWith("--tdd: the re-run limit (3) was reached with 2 member(s) generated into another bundle and not compiled in:", limit);
        Assert.Contains(M4, limit);
        Assert.Contains(M5, limit);

        var heading = lines.FindIndex(l => l.Contains(" member(s) this run:", StringComparison.Ordinal));
        Assert.True(heading >= 0, run.StdErr);
        Assert.Equal("--tdd: generated 3 member(s) this run:", lines[heading]);
        var listed = lines.Skip(heading + 1).TakeWhile(l => l.StartsWith("  Lib Chain ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, listed.Count);
        Assert.DoesNotContain(listed, l => l.Contains("\"M4\"") || l.Contains("\"M5\""));

        var closing = lines.FindIndex(l => l.StartsWith("--tdd: 2 more member(s) were generated into another bundle but not compiled in", StringComparison.Ordinal));
        Assert.True(closing > heading, run.StdErr);
        Assert.Equal(new[] { $"  {M4}", $"  {M5}" }, lines.Skip(closing + 1).Take(2).ToArray());
    }

    [SkippableFact]
    public void LongChainInOrder_ReportsTheLimitInsteadOfADependencyLoadFatal()
    {
        TestArtifacts.SkipIfMissing();
        AssertReportsTheLimit(_inOrder, "app lib1 lib2 lib3 lib4 test");
    }

    [SkippableFact]
    public void LongChainReversed_ReportsTheLimitInsteadOfADependencyLoadFatal()
    {
        TestArtifacts.SkipIfMissing();
        AssertReportsTheLimit(_reversed, "test lib4 lib3 lib2 lib1 app");
    }

    /// <summary>
    /// The limit does not trigger early: a chain of exactly three re-runs still generates every member,
    /// the test runs against them and passes, and nothing says the limit was reached.
    /// </summary>
    [SkippableFact]
    public void ChainOfExactlyTheLimit_GeneratesEveryMemberAndPasses()
    {
        TestArtifacts.SkipIfMissing();
        Assert.True(_atTheLimit.Exit == 0, _atTheLimit.StdErr);
        Assert.Equal("pass", _atTheLimit.Find("Reaches").GetProperty("status").GetString());
        Assert.Equal(new[] { "Lib Chain 2: procedure \"M3\"(Arg1: Integer): Integer" }, _atTheLimit.StubsOf("Reaches"));
        Assert.Contains("--tdd: generated 3 member(s) this run:", _atTheLimit.StdErr);
        Assert.DoesNotContain("re-run limit", _atTheLimit.StdErr);
        Assert.DoesNotContain("not compiled in", _atTheLimit.StdErr);
        Assert.DoesNotContain("FATAL", _atTheLimit.StdErr);
    }
}
