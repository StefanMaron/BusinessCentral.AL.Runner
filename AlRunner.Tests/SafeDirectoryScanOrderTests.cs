// SafeDirectoryScanOrderTests — the ordering half of SafeDirectoryScan's contract (#2872).
//
// SafeDirectoryScan.Files is how every AL compile in this runner collects its *.al inputs
// (BcCompiler.Emit, EmitDepSymbols, the three incremental paths, InProcessAppPackager, ...).
// It returned whatever order the filesystem's readdir happened to produce, and its own walk
// popped subdirectories off a Stack, so the order was reversed relative to
// Directory.GetDirectories on top of that.
//
// That order is not cosmetic. It is the order of the syntax trees handed to the AL compiler,
// therefore the TypeDef order of the emitted assembly, therefore the order Assembly.GetTypes()
// returns, therefore the order TestExecutor runs test codeunits in. Two directories holding
// byte-identical AL sources, differing only in the order their files were created, produced
// assemblies that ran the same tests in different orders — measured on this machine, and it is
// what turned `main` red on the BC 27.5 leg (run 33984312053): a watchdog abort ends the run,
// so which codeunit had already run decided what the JUnit contained.
//
// The mirror image of the bug is the reason it has to be fixed HERE rather than at one call
// site: ComputeAlCacheKey (ProgramSupport/Dependencies.cs) already sorts the very same file
// list — "Enumerate every .al file in stable order" — before hashing it. So the KEY was
// order-independent while the COMPILE was not: two bundles with identical content hash to one
// cache entry and can legitimately want two different assemblies out of it. A cache HIT then
// serves whichever order was emitted first, for every later run.
//
// Ordinal, not culture: the whole point is an order that does not move between machines.
using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class SafeDirectoryScanOrderTests : IDisposable
{
    private readonly string _root;

    public SafeDirectoryScanOrderTests()
    {
        _root = TestScratch.Dir("al-runner-scan-order");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Positive, flat: files created in reverse-alphabetical order come back in ORDINAL
    /// order, not creation order. On ext4/tmpfs a small directory's readdir is creation
    /// order, so pre-fix this returned [c, b, a] — the exact shape that flipped the two
    /// fixture codeunits on CI.
    /// </summary>
    [Fact]
    public void Files_AreReturnedInOrdinalOrder_NotCreationOrder()
    {
        foreach (var n in new[] { "ccc.al", "bbb.al", "aaa.al" })
            File.WriteAllText(Path.Combine(_root, n), "x");
        // Not a *.al file: it must not appear at all, so a "sorted" result cannot be
        // achieved by returning everything and letting the caller filter.
        File.WriteAllText(Path.Combine(_root, "app.json"), "{}");

        var hits = SafeDirectoryScan.Files(_root, "*.al");

        Assert.Equal(
            new[] { "aaa.al", "bbb.al", "ccc.al" },
            hits.Select(Path.GetFileName).ToArray());
    }

    /// <summary>
    /// Positive, nested: the walk's Stack popped subdirectories in reverse, so this failed
    /// independently of how readdir ordered anything. Full paths, ordinal — which puts a
    /// parent's own files before its subdirectories' and keeps sibling directories in name
    /// order.
    /// </summary>
    [Fact]
    public void Files_AcrossSubdirectories_AreReturnedInOrdinalPathOrder()
    {
        foreach (var sub in new[] { "alpha", "beta", "gamma" })
        {
            Directory.CreateDirectory(Path.Combine(_root, sub));
            File.WriteAllText(Path.Combine(_root, sub, "One.al"), "x");
            File.WriteAllText(Path.Combine(_root, sub, "Two.al"), "x");
        }
        File.WriteAllText(Path.Combine(_root, "Root.al"), "x");

        var hits = SafeDirectoryScan.Files(_root, "*.al");

        Assert.Equal(
            hits.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            hits.ToArray());
        Assert.Equal(
            new[]
            {
                "Root.al",
                Path.Combine("alpha", "One.al"), Path.Combine("alpha", "Two.al"),
                Path.Combine("beta", "One.al"), Path.Combine("beta", "Two.al"),
                Path.Combine("gamma", "One.al"), Path.Combine("gamma", "Two.al"),
            },
            hits.Select(p => Path.GetRelativePath(_root, p)).ToArray());
    }

    /// <summary>
    /// The same guarantee for the directory overload — <c>.alpackages</c> discovery feeds
    /// dependency resolution, where "the first one found" decides which package wins.
    /// </summary>
    [Fact]
    public void Directories_AreReturnedInOrdinalPathOrder()
    {
        foreach (var sub in new[] { "zeta", "delta", "alpha" })
            Directory.CreateDirectory(Path.Combine(_root, sub, ".alpackages"));

        var hits = SafeDirectoryScan.Directories(_root, ".alpackages");

        Assert.Equal(
            new[]
            {
                Path.Combine("alpha", ".alpackages"),
                Path.Combine("delta", ".alpackages"),
                Path.Combine("zeta", ".alpackages"),
            },
            hits.Select(p => Path.GetRelativePath(_root, p)).ToArray());
    }

    /// <summary>
    /// Negative: sorting must not change WHAT is found. An empty result stays empty, and a
    /// pattern that matches nothing does not start matching something because the list is
    /// now sorted.
    /// </summary>
    [Fact]
    public void Sorting_DoesNotChangeWhatIsFound()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "only.al"), "x");

        Assert.Empty(SafeDirectoryScan.Files(_root, "*.xlf"));
        Assert.Empty(SafeDirectoryScan.Directories(_root, ".alpackages"));
        Assert.Single(SafeDirectoryScan.Files(_root, "*.al"));
        Assert.Equal(
            Path.Combine("sub", "only.al"),
            Path.GetRelativePath(_root, SafeDirectoryScan.Files(_root, "*.al")[0]));
    }
}
