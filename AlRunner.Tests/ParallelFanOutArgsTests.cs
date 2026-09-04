// ParallelFanOutArgsTests — the child command line --jobs builds (issue #2280).
//
// Arg rewriting is where a fan-out quietly goes wrong: drop a flag and every worker runs a
// DIFFERENT configuration from the one the user asked for, and the aggregate number is not
// measuring what its caller believes. So each of these pins one specific thing that must
// survive, or must not.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutArgsTests
{
    /// <summary>Every flag the user passed reaches the worker. A worker that silently drops
    /// --test-data runs a different suite and reports a much lower pass count.</summary>
    [Fact]
    public void BuildChildArgs_KeepsEveryFlagAndItsValue()
    {
        var child = ParallelFanOut.BuildChildArgs(
            new[] { "/b/one", "--test-data", "--test-data-company", "CRONUS Ltd_", "/b/two", "--jobs", "4" },
            new[] { "/b/one" }, bundleRoots: new[] { "/b/one", "/b/two" }, junitPath: "/tmp/s0.xml");

        Assert.Contains("--test-data", child);
        Assert.Contains("--test-data-company", child);
        Assert.Contains("CRONUS Ltd_", child);
    }

    /// <summary>--jobs must NOT reach the worker, or every worker fans out again — an
    /// exponential process bomb rather than a parallel run.</summary>
    [Fact]
    public void BuildChildArgs_StripsJobsAndItsValue()
    {
        var child = ParallelFanOut.BuildChildArgs(
            new[] { "/b/one", "--jobs", "6", "--verbose" },
            new[] { "/b/one" }, bundleRoots: new[] { "/b/one" }, junitPath: "/tmp/s0.xml");

        Assert.DoesNotContain("--jobs", child);
        Assert.DoesNotContain("6", child);
        Assert.Contains("--verbose", child);
    }

    /// <summary>Only this shard's bundles are passed — the other shards' bundles must be gone,
    /// or every worker runs everything and the aggregate double-counts.</summary>
    [Fact]
    public void BuildChildArgs_PassesOnlyItsOwnShardsBundles()
    {
        var child = ParallelFanOut.BuildChildArgs(
            new[] { "/b/one", "/b/two", "/b/three" },
            new[] { "/b/two" }, bundleRoots: new[] { "/b/one", "/b/two", "/b/three" }, junitPath: "/tmp/s1.xml");

        Assert.Contains("/b/two", child);
        Assert.DoesNotContain("/b/one", child);
        Assert.DoesNotContain("/b/three", child);
    }

    /// <summary>The worker writes JUnit to the parent's temp path, which is how the parent
    /// aggregates. A user-supplied --output-junit must not survive alongside it: two
    /// --output-junit values would leave the last one winning and the parent reading a file the
    /// worker never wrote, or the user's own report holding one shard's results.</summary>
    [Fact]
    public void BuildChildArgs_ReplacesAnyUserSuppliedJunitPathWithTheShardPath()
    {
        var child = ParallelFanOut.BuildChildArgs(
            new[] { "/b/one", "--output-junit", "/user/report.xml" },
            new[] { "/b/one" }, bundleRoots: new[] { "/b/one" }, junitPath: "/tmp/s0.xml");

        Assert.Single(child, a => a == "--output-junit");
        Assert.Contains("/tmp/s0.xml", child);
        Assert.DoesNotContain("/user/report.xml", child);
    }

    /// <summary>A flag VALUE that happens to equal a bundle path must not be mistaken for a
    /// positional bundle and stripped. --cache pointed at a bundle dir is unusual but legal, and
    /// silently dropping it changes where every worker caches.</summary>
    [Fact]
    public void BuildChildArgs_DoesNotStripAFlagValueThatLooksLikeABundlePath()
    {
        var child = ParallelFanOut.BuildChildArgs(
            new[] { "/b/one", "--cache", "/b/two" },
            new[] { "/b/one" }, bundleRoots: new[] { "/b/one", "/b/two" }, junitPath: "/tmp/s0.xml");

        Assert.Contains("--cache", child);
        Assert.Contains("/b/two", child);
    }
}
