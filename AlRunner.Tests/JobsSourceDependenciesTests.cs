// JobsSourceDependenciesTests — which source folders a `--jobs` worker is handed beside its own
// bundles, compiles, and does not run (#5267). The graph half is pure, so each property is pinned
// here without a runner process; JobsSourceDependencyEndToEndTests is the oracle against a plain run.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsSourceDependenciesTests
{
    // never created: these tests only compare paths
    private static readonly string Root = Path.Combine(Path.GetFullPath(Path.DirectorySeparatorChar.ToString()), "jobs-source-deps-pure");

    private static string P(string name) => Path.Combine(Root, name);

    private static BundleIdentity App(string name, params string[] dependsOn)
        => new(
            AppId: Guid.NewGuid(), Name: name, Publisher: "AL Runner Fixtures", Version: new Version(1, 0, 0, 0),
            RuntimeVersion: new Version(13, 0),
            Dependencies: dependsOn
                .Select(n => new DependencyRef(Guid.Empty, n, "AL Runner Fixtures", new Version(1, 0, 0, 0), false))
                .ToList());

    /// <summary>The folders listed on the command line, in order, each named like its app.</summary>
    private static (List<string> All, Func<string, BundleIdentity?> IdentityOf) Run(
        params (string Name, string[] DependsOn)[] apps)
    {
        var byPath = apps.ToDictionary(a => ParallelFanOut.Normalize(P(a.Name)), a => App(a.Name, a.DependsOn),
            StringComparer.OrdinalIgnoreCase);
        return (apps.Select(a => P(a.Name)).ToList(),
            p => byPath.TryGetValue(ParallelFanOut.Normalize(p), out var id) ? id : null);
    }

    private static string[] Outside(string[] shard, (List<string> All, Func<string, BundleIdentity?> IdentityOf) run)
        => JobsSourceDependencies.OutsideShard(shard.Select(P).ToList(), run.All, run.IdentityOf)
            .Select(p => Path.GetFileName(p)).ToArray();

    [Fact]
    public void ADirectDependencyOnAnotherShard_IsHandedToTheWorker()
    {
        var run = Run(("lib", Array.Empty<string>()), ("test", new[] { "lib" }));

        Assert.Equal(new[] { "lib" }, Outside(new[] { "test" }, run));
    }

    [Fact]
    public void TheDependencyItself_NeedsNothing()
    {
        var run = Run(("lib", Array.Empty<string>()), ("test", new[] { "lib" }));

        Assert.Empty(Outside(new[] { "lib" }, run));
    }

    /// <summary>top -> mid -> base with the shard holding only top: base is reached through mid, a
    /// folder the shard does not hold either. Without following it the worker has mid and no base.</summary>
    [Fact]
    public void ADependencyOfADependencyTheShardDoesNotHold_IsHandedToo_InTheRunsOrder()
    {
        var run = Run(("base", Array.Empty<string>()), ("mid", new[] { "base" }), ("top", new[] { "mid" }));

        Assert.Equal(new[] { "base", "mid" }, Outside(new[] { "top" }, run));
    }

    /// <summary>The same chain, with mid on the shard: base is mid's dependency, so the shard needs it
    /// for mid's own compile even though nothing the shard holds names it directly.</summary>
    [Fact]
    public void ADependencyOfAFolderTheShardHolds_IsHanded()
    {
        var run = Run(("base", Array.Empty<string>()), ("mid", new[] { "base" }), ("top", new[] { "mid" }));

        Assert.Equal(new[] { "base" }, Outside(new[] { "top", "mid" }, run));
    }

    [Fact]
    public void ADiamond_ListsEachFolderOnce()
    {
        var run = Run(("base", Array.Empty<string>()), ("left", new[] { "base" }), ("right", new[] { "base" }),
            ("top", new[] { "left", "right", "base" }));

        Assert.Equal(new[] { "base", "left", "right" }, Outside(new[] { "top" }, run));
    }

    [Fact]
    public void AFolderTheShardAlreadyHolds_IsNotHandedAgain()
    {
        var run = Run(("lib", Array.Empty<string>()), ("test", new[] { "lib" }));

        Assert.Empty(Outside(new[] { "lib", "test" }, run));
    }

    /// <summary>A dependency that is not among the listed folders (a precompiled package) is the
    /// pre-pass's business exactly as without --jobs: nothing is added for it.</summary>
    [Fact]
    public void ADependencyThatIsNotAListedFolder_AddsNothing()
    {
        var run = Run(("test", new[] { "Microsoft Base Application" }), ("other", Array.Empty<string>()));

        Assert.Empty(Outside(new[] { "test" }, run));
    }

    [Fact]
    public void AnUnrelatedFolder_IsNotPulledIn()
    {
        var run = Run(("lib", Array.Empty<string>()), ("test", new[] { "lib" }), ("solo", Array.Empty<string>()));

        Assert.Equal(new[] { "lib" }, Outside(new[] { "test" }, run));
        Assert.Empty(Outside(new[] { "solo" }, run));
    }

    /// <summary>The dependency list of a folder that fails once it has been read more often than any
    /// walk of a graph this small needs: a walk that revisits folders reads it without end, and would
    /// otherwise hang the test host and grow its queue until the box ran out of memory.</summary>
    private sealed class BoundedDependencies : IReadOnlyList<DependencyRef>
    {
        private readonly IReadOnlyList<DependencyRef> _inner;
        private int _reads;

        public BoundedDependencies(IReadOnlyList<DependencyRef> inner) => _inner = inner;

        public IEnumerator<DependencyRef> GetEnumerator()
        {
            if (++_reads > 1_000)
                throw new InvalidOperationException("the walk revisited a folder it had already visited");
            return _inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => _inner.Count;
        public DependencyRef this[int index] => _inner[index];
    }

    /// <summary>A dependency cycle is not this code's to adjudicate: the walk visits each folder once,
    /// terminates, and names the other folder.</summary>
    [Fact]
    public void ACycle_VisitsEachFolderOnce_AndTerminates()
    {
        var run = Run(("a", new[] { "b" }), ("b", new[] { "a" }));
        var bounded = run.All.ToDictionary(p => ParallelFanOut.Normalize(p),
            p => { var id = run.IdentityOf(p)!; return id with { Dependencies = new BoundedDependencies(id.Dependencies) }; },
            StringComparer.OrdinalIgnoreCase);

        var outside = JobsSourceDependencies.OutsideShard(new[] { P("a") }, run.All,
            p => bounded[ParallelFanOut.Normalize(p)]);

        Assert.Equal(new[] { P("b") }, outside);
    }

    [Fact]
    public void AFolderWithNoReadableIdentity_RelatesToNothing()
    {
        var all = new List<string> { P("lib"), P("noid"), P("test") };
        var ids = new Dictionary<string, BundleIdentity?>(StringComparer.OrdinalIgnoreCase)
        {
            [ParallelFanOut.Normalize(P("lib"))] = App("lib"),
            [ParallelFanOut.Normalize(P("test"))] = App("test", "lib"),
        };

        var outside = JobsSourceDependencies.OutsideShard(new[] { P("test") }, all,
            p => ids.GetValueOrDefault(ParallelFanOut.Normalize(p)));

        Assert.Equal(new[] { P("lib") }, outside);
        Assert.Empty(JobsSourceDependencies.OutsideShard(new[] { P("noid") }, all,
            p => ids.GetValueOrDefault(ParallelFanOut.Normalize(p))));
    }

    /// <summary>Matched by the declared AppId when there is one, and by Name+Publisher otherwise: the
    /// pair the layered pre-pass matches on, so the two cannot disagree on what a dependency is.</summary>
    [Fact]
    public void ADependencyIsMatchedByAppId_AsWellAsByName()
    {
        var libId = Guid.NewGuid();
        var lib = App("lib") with { AppId = libId };
        var test = App("test") with
        {
            Dependencies = new List<DependencyRef> { new(libId, "renamed", "someone else", new Version(1, 0, 0, 0), false) },
        };
        var all = new List<string> { P("lib"), P("test") };

        var outside = JobsSourceDependencies.OutsideShard(new[] { P("test") }, all,
            p => Path.GetFileName(p) == "lib" ? lib : test);

        Assert.Equal(new[] { P("lib") }, outside);
    }

    [Fact]
    public void TheHandOff_RoundTrips_AndAnUnsetValueMarksNothing()
    {
        var withSpace = Path.Combine(Root, "a folder", "x");
        var parsed = JobsSourceDependencies.Parse(JobsSourceDependencies.Format(new[] { withSpace, P("lib") }));

        Assert.Equal(2, parsed.Count);
        Assert.Contains(ParallelFanOut.Normalize(withSpace), parsed);
        Assert.Contains(ParallelFanOut.Normalize(P("lib")), parsed);
        Assert.Empty(JobsSourceDependencies.Parse(null));
        Assert.Empty(JobsSourceDependencies.Parse(""));
    }

    /// <summary>A value already in the parent's environment must not reach a worker that has no
    /// dependency-only folder: it would stop that worker running folders it was handed to run.</summary>
    [Fact]
    public void TheWorkerEnvironment_IsAlwaysSet_SoAnInheritedValueIsOverwritten()
    {
        var env = new Dictionary<string, string?> { [JobsSourceDependencies.DependencyOnlyEnvVar] = P("inherited") };

        JobsSourceDependencies.ApplyTo(env, Array.Empty<string>());
        Assert.Empty(JobsSourceDependencies.Parse(env[JobsSourceDependencies.DependencyOnlyEnvVar]));

        JobsSourceDependencies.ApplyTo(env, new[] { P("lib") });
        Assert.Equal(new[] { ParallelFanOut.Normalize(P("lib")) },
            JobsSourceDependencies.Parse(env[JobsSourceDependencies.DependencyOnlyEnvVar]));
    }

    [Fact]
    public void RunBundles_DropsOnlyTheMarkedFolders_AndIsTheSameListWhenNothingIsMarked()
    {
        var bundles = new List<string> { P("lib"), P("test") };

        Assert.Same(bundles, JobsSourceDependencies.RunBundles(bundles, JobsSourceDependencies.Parse(null)));
        // the marked path is written differently from the argument: a trailing separator
        var marked = JobsSourceDependencies.Parse(P("lib") + Path.DirectorySeparatorChar);
        Assert.Equal(new[] { P("test") }, JobsSourceDependencies.RunBundles(bundles, marked));
    }

    // ── the plan itself is unchanged: a dependency does not glue folders together ───────────────

    private static List<string> TempFolders(string name, params (string Folder, int Files, string[] DependsOn)[] apps)
    {
        var root = TestScratch.Dir("al-runner-jobs-source-deps-plan-" + name);
        var paths = new List<string>();
        foreach (var (folder, files, dependsOn) in apps)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            for (var i = 0; i < files; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.al"), "");
            var deps = string.Join(",", dependsOn.Select(d =>
                $"{{\"id\":\"{DeterministicId(d)}\",\"name\":\"{d}\",\"publisher\":\"P\",\"version\":\"1.0.0.0\"}}"));
            File.WriteAllText(Path.Combine(dir, "app.json"),
                $"{{\"id\":\"{DeterministicId(folder)}\",\"name\":\"{folder}\",\"publisher\":\"P\",\"version\":\"1.0.0.0\","
                + $"\"dependencies\":[{deps}],\"runtime\":\"14.0\"}}");
            paths.Add(dir);
        }
        return paths;
    }

    private static Guid DeterministicId(string s)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    /// <summary>The positive control for not collapsing a closure into one unit of work: four equal
    /// folders, one depending on another, still land two to a worker; the dependent's worker is handed
    /// the dependency on top of its own bundles, and the folders no one depends on are handed nothing.</summary>
    [Fact]
    public void FoldersThatDependOnEachOther_AreStillSpreadAcrossWorkers_ByWeightAlone()
    {
        var folders = TempFolders("spread",
            ("a", 2, Array.Empty<string>()), ("b", 2, new[] { "a" }), ("c", 2, Array.Empty<string>()), ("d", 2, Array.Empty<string>()));

        var plan = ParallelFanOut.PlanBundles(folders, 2, null, null, JobsMemory.Model);

        Assert.Equal(2, plan.Shards.Count);
        Assert.All(plan.Shards, s => Assert.Equal(2, s.Count));
        var shardOfA = plan.Shards.Single(s => s.Any(x => x.Name == folders[0]));
        Assert.DoesNotContain(plan.Shards, s => s.Any(x => x.Name == folders[0]) && s.Any(x => x.Name == folders[1]));
        var shardOfB = plan.Shards.Single(s => s.Any(x => x.Name == folders[1]));
        Assert.Equal(new[] { folders[0] },
            JobsSourceDependencies.OutsideShard(shardOfB.Select(x => x.Name).ToList(), folders));
        Assert.Empty(JobsSourceDependencies.OutsideShard(shardOfA.Select(x => x.Name).ToList(), folders));
    }
}
