// JobsReportsMergeTests — the pure parts of `--jobs` writing the caller's --out and --output-json (#5129):
// what a worker is and is not handed, and how the workers' results fold into the run's buckets. The
// end-to-end half, which spawns real workers and compares with a plain run, is JobsReportsEndToEndTests.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsReportsMergeTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("jobsreports-");

    public JobsReportsMergeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── what a worker is handed ──────────────────────────────────────────────────────────────

    private static List<string> Child(params string[] args)
        => ParallelFanOut.BuildChildArgs(args, new[] { "/b/one" }, new[] { "/b/one", "/b/two" }, "/s/shard-0.xml");

    /// <summary>The caller's --out and --output-json belong to the RUN: a worker that kept them would write the
    /// one --out path itself (the last to finish wins) and print its own document. The path is dropped with
    /// the flag, and a value that looks like a bundle is not read as one.</summary>
    [Fact]
    public void BuildChildArgs_DropsOutAndItsPath_AndOutputJson_AndKeepsTheOtherFlags()
    {
        var child = Child("--jobs", "2", "--out", "/b/two", "--output-json", "--cache", "/c", "/b/one", "/b/two");

        Assert.DoesNotContain("--out", child);
        Assert.DoesNotContain("--output-json", child);
        // /b/two is --out's value here, not a bundle: it went with the flag, and the worker's own bundle is /b/one
        Assert.Equal(new[] { "--cache", "/c", "--classify", "/b/one", "--output-junit", "/s/shard-0.xml" }, child);
    }

    /// <summary>--out also switched the FAILURE CLASSIFICATION block on, and each shard's section still prints
    /// it; a run that never named --out gets no extra flag.</summary>
    [Fact]
    public void BuildChildArgs_KeepsTheClassificationBlockOnlyWhenOutWasNamed()
    {
        Assert.Contains("--classify", Child("--out", "r.json", "/b/one"));
        Assert.DoesNotContain("--classify", Child("--output-json", "/b/one"));
        Assert.DoesNotContain("--classify", Child("/b/one"));
    }

    /// <summary>A worker forced to exit 0 would hide its verdict from the parent, whose Result line and
    /// exitCode would say a failing run passed; the parent applies it to the run's own code.</summary>
    [Fact]
    public void BuildChildArgs_DropsNoStrictExit_ButNotStrict()
    {
        var child = Child("--no-strict-exit", "--strict", "/b/one");

        Assert.DoesNotContain("--no-strict-exit", child);
        Assert.Contains("--strict", child);
    }

    [Fact]
    public void StrictExit_TheLastOfTheTwoFlagsWins_AsInTheSingleProcessRun()
    {
        Assert.True(ParallelFanOut.StrictExit(new[] { "/b/one" }));
        Assert.False(ParallelFanOut.StrictExit(new[] { "--no-strict-exit", "/b/one" }));
        Assert.True(ParallelFanOut.StrictExit(new[] { "--no-strict-exit", "--strict" }));
        Assert.False(ParallelFanOut.StrictExit(new[] { "--strict", "--no-strict-exit" }));
    }

    // ── folding the workers' results ─────────────────────────────────────────────────────────

    private static TestResult Test(string codeunit, string method, TestOutcome outcome = TestOutcome.Pass,
        IReadOnlyList<string>? stubs = null)
        => new(codeunit, method, outcome, outcome == TestOutcome.Pass ? null : "boom", null, TimeSpan.FromMilliseconds(5),
            GeneratedStubs: stubs);

    private static BucketResult Bucket(string path, params TestResult[] tests)
        => new(path, BucketStage.Ran, Array.Empty<string>(), null, tests, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1);

    private string Results(string name, params BucketResult[] buckets)
    {
        var p = Path.Combine(_dir, name);
        ResumeCarry.Write(p, buckets);
        return p;
    }

    private static IReadOnlyList<string> Ended(int n) => Enumerable.Range(0, n).Select(i => $"exit {i}").ToList();

    /// <summary>Each bundle is in the order the caller named them, whatever shard ran it: shard 0 holds the
    /// bundle named LAST here.</summary>
    [Fact]
    public void Merge_ListsBundlesInTheOrderTheCallerNamedThem_NotInShardOrder()
    {
        var shard0 = Results("s0.json", Bucket("/b/three", Test("Codeunit3", "T")));
        var shard1 = Results("s1.json", Bucket("/b/one", Test("Codeunit1", "T")), Bucket("/b/two", Test("Codeunit2", "T")));

        var (buckets, lost) = JobsReports.Merge(new[] { shard0, shard1 },
            new[] { new[] { "/b/three" }, new[] { "/b/one", "/b/two" } },
            new[] { "/b/one", "/b/two", "/b/three" }, Ended(2));

        Assert.Empty(lost);
        Assert.Equal(new[] { "/b/one", "/b/two", "/b/three" }, buckets.Select(b => b.BucketPath));
    }

    /// <summary>A bundle several workers share is ONE bucket holding every test each worker claimed: read as
    /// one bucket per worker, --out would list its suite error once per worker.</summary>
    [Fact]
    public void Merge_ASharedBundle_IsOneBucketHoldingEveryWorkersTests()
    {
        var a = Bucket("/b/shared", Test("Codeunit1", "A", TestOutcome.Fail), Test("Codeunit2", "B"));
        var b = Bucket("/b/shared", Test("Codeunit3", "C"), Test("Codeunit4", "D", TestOutcome.Fail));

        var (buckets, lost) = JobsReports.Merge(new[] { Results("s0.json", a), Results("s1.json", b) },
            new[] { new[] { "/b/shared" }, new[] { "/b/shared" } }, new[] { "/b/shared" }, Ended(2));

        Assert.Empty(lost);
        var only = Assert.Single(buckets);
        Assert.Equal(new[] { "A", "B", "C", "D" }, only.Tests.Select(t => t.Method));
    }

    /// <summary>A worker that handed nothing back (killed, crashed before its output block, a torn file) is
    /// reported, and each bundle it was given is a failed-to-execute bucket saying how it ended: left out, the
    /// report would read as a run that never had those bundles. The worker that did report is untouched.</summary>
    [Fact]
    public void Merge_AShardWithNoReadableResults_BecomesExecuteFailedBucketsNamingHowItEnded()
    {
        var ok = Results("s0.json", Bucket("/b/one", Test("Codeunit1", "T")));
        var torn = Path.Combine(_dir, "s1.json");
        File.WriteAllText(torn, "[{\"BucketPath\":");
        var absent = Path.Combine(_dir, "s2.json");

        var (buckets, lost) = JobsReports.Merge(new[] { ok, torn, absent },
            new[] { new[] { "/b/one" }, new[] { "/b/two", "/b/three" }, new[] { "/b/four" } },
            new[] { "/b/one", "/b/two", "/b/three", "/b/four" }, new[] { "exit 0", "exit 137", "killed" });

        Assert.Equal(new[] { 1, 2 }, lost);
        Assert.Equal(BucketStage.Ran, buckets[0].Stage);
        Assert.Equal(new[] { "/b/two", "/b/three", "/b/four" }, buckets.Skip(1).Select(b => b.BucketPath));
        Assert.All(buckets.Skip(1), b => Assert.Equal(BucketStage.ExecuteFailed, b.Stage));
        Assert.Contains("worker 1 ended (exit 137)", buckets[1].ProcessError);
        Assert.Contains("worker 2 ended (killed)", buckets[3].ProcessError);
        Assert.Empty(buckets.Skip(1).SelectMany(b => b.Tests));
    }

    /// <summary>--output-json's `generatedStubs` crosses the process boundary with the rest of the result: without
    /// it a `--tdd --jobs` document silently loses which generated members each test reached.</summary>
    [Fact]
    public void ResumeCarry_CarriesAnyGeneratedStubsOfATest()
    {
        var stubs = new[] { "Points: procedure \"CalcLit\"(Arg1: Integer): Integer" };
        var path = Results("stubs.json", Bucket("/b/one", Test("Codeunit1", "WithStub", stubs: stubs), Test("Codeunit1", "NoStub")));

        var tests = ResumeCarry.Read(new[] { path }, out var unreadable).Single().Tests;

        Assert.Equal(0, unreadable);
        Assert.Equal(stubs, tests.Single(t => t.Method == "WithStub").GeneratedStubs);
        Assert.Null(tests.Single(t => t.Method == "NoStub").GeneratedStubs);
    }
}
