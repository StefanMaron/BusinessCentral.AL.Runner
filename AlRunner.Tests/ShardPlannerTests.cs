// ShardPlannerTests — how --jobs splits bundles across worker processes (issue #2280).
//
// Why balance matters here rather than "one bundle per worker": Microsoft's BaseApp buckets
// differ by two orders of magnitude (Tests-Upgrade has 11 tests, Tests-ERM has 9,500). Handing
// each worker an equal COUNT of bundles leaves one worker running ERM while the rest idle, and
// the run takes as long as its longest shard no matter how many cores are free.
//
// The weight is a caller-supplied proxy for how long a bundle takes. Longest-processing-time
// assignment (heaviest first, always to the currently lightest shard) is the standard greedy
// bound for this and is deterministic, which matters because a shard plan that reshuffles
// between runs makes a per-shard timing regression unreadable.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ShardPlannerTests
{
    private static (string Name, long Weight)[] Items(params (string, long)[] xs) => xs;

    /// <summary>The whole point: heavy items are spread, not clustered on one worker.</summary>
    [Fact]
    public void Plan_BalancesByWeight_NotByItemCount()
    {
        var plan = ShardPlanner.Plan(
            Items(("ERM", 9500), ("SCM", 8526), ("Misc", 3197), ("Upgrade", 11)), jobs: 2);

        Assert.Equal(2, plan.Count);
        var weights = plan.Select(s => s.Sum(i => i.Weight)).OrderBy(w => w).ToList();
        // Perfect split is impossible; the point is the two heaviest never land together.
        var ermShard = plan.Single(s => s.Any(i => i.Name == "ERM"));
        Assert.DoesNotContain(ermShard, s => s.Name == "SCM");
        Assert.True(weights[1] - weights[0] < 9500,
            $"shards are wildly unbalanced: {string.Join(" / ", weights)}");
    }

    /// <summary>Every item lands exactly once — a shard plan that drops a bundle silently
    /// loses its whole test set, which is the failure this must never have.</summary>
    [Fact]
    public void Plan_AssignsEveryItemExactlyOnce()
    {
        var items = Items(("a", 5), ("b", 4), ("c", 3), ("d", 2), ("e", 1));
        var plan = ShardPlanner.Plan(items, jobs: 3);

        var all = plan.SelectMany(s => s.Select(i => i.Name)).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, all.OrderBy(x => x).ToArray());
    }

    /// <summary>Never more shards than items: an empty shard would spawn a worker process that
    /// pays the full BC boot cost to run nothing.</summary>
    [Fact]
    public void Plan_NeverProducesAnEmptyShard()
    {
        var plan = ShardPlanner.Plan(Items(("only", 1), ("two", 1)), jobs: 8);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, s => Assert.NotEmpty(s));
    }

    /// <summary>jobs of 1 (and anything lower) is exactly today's behaviour: one shard, original
    /// order preserved, so --jobs 1 is not a different code path with different results.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-4)]
    public void Plan_OneJobOrFewer_IsASingleShardInOriginalOrder(int jobs)
    {
        var plan = ShardPlanner.Plan(Items(("a", 1), ("b", 99), ("c", 5)), jobs);

        Assert.Single(plan);
        Assert.Equal(new[] { "a", "b", "c" }, plan[0].Select(i => i.Name).ToArray());
    }

    /// <summary>Deterministic across calls, including for equal weights, where a stable
    /// tie-break is the only thing that can decide. A plan that reshuffles between runs makes a
    /// per-shard timing regression impossible to read.</summary>
    [Fact]
    public void Plan_IsDeterministic_EvenWhenWeightsTie()
    {
        var items = Items(("d", 7), ("a", 7), ("c", 7), ("b", 7));

        var first = ShardPlanner.Plan(items, 2).Select(s => s.Select(i => i.Name).ToArray()).ToArray();
        var second = ShardPlanner.Plan(items, 2).Select(s => s.Select(i => i.Name).ToArray()).ToArray();

        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++) Assert.Equal(first[i], second[i]);
    }

    /// <summary>Zero-weight items (a bundle whose weight could not be measured) must still be
    /// scheduled rather than silently dropped or all piled onto shard 0.</summary>
    [Fact]
    public void Plan_SchedulesZeroWeightItems()
    {
        var plan = ShardPlanner.Plan(Items(("heavy", 100), ("z1", 0), ("z2", 0), ("z3", 0)), jobs: 2);

        var all = plan.SelectMany(s => s.Select(i => i.Name)).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "heavy", "z1", "z2", "z3" }, all);
    }

    [Fact]
    public void Plan_EmptyInput_ProducesNoShards()
    {
        Assert.Empty(ShardPlanner.Plan(System.Array.Empty<(string, long)>(), jobs: 4));
    }

    // ── PlanSplit (#5130): a bundle heavier than one worker's share is cut across workers ──

    [Fact]
    public void PlanSplit_OneHeavyBundle_IsSharedByEveryWorker_OnDistinctShards()
    {
        var plan = ShardPlanner.PlanSplit(Items(("big", 1000)), jobs: 4, minPieceWeight: 100);

        Assert.Equal(4, plan.Shards.Count);
        Assert.All(plan.Shards, s => Assert.Equal("big", Assert.Single(s).Name));
        Assert.Equal(new[] { "big" }, plan.SplitBundles.ToArray());
    }

    /// <summary>A piece lighter than the minimum is not worth a worker's startup and load: the
    /// number of pieces is capped, not the number of workers asked for.</summary>
    [Fact]
    public void PlanSplit_NeverCutsBelowTheMinimumPieceWeight()
    {
        var plan = ShardPlanner.PlanSplit(Items(("big", 1000)), jobs: 8, minPieceWeight: 400);

        Assert.Equal(2, plan.Shards.Count);
        Assert.Equal(new[] { "big" }, plan.SplitBundles.ToArray());
    }

    [Fact]
    public void PlanSplit_BundleUnderTheMinimum_IsNotSplit_AndMatchesPlanExactly()
    {
        var items = Items(("a", 50), ("b", 40));
        var plan = ShardPlanner.PlanSplit(items, jobs: 2, minPieceWeight: 100);

        Assert.Empty(plan.SplitBundles);
        var expected = ShardPlanner.Plan(items, 2).Select(s => s.Select(i => i.Name).ToArray()).ToArray();
        var actual = plan.Shards.Select(s => s.Select(i => i.Name).ToArray()).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>Balanced bundles are the case bundle-level sharding already handles; splitting
    /// one of them would only multiply its load cost.</summary>
    [Fact]
    public void PlanSplit_BalancedBundles_AreNotSplit()
    {
        var plan = ShardPlanner.PlanSplit(Items(("a", 500), ("b", 500), ("c", 500), ("d", 500)), jobs: 4, 100);

        Assert.Empty(plan.SplitBundles);
        Assert.Equal(4, plan.Shards.Count);
    }

    /// <summary>The Tests-ERM shape: one bundle far above the fair share, several small ones. Only the
    /// big one is cut, and every other bundle is still placed exactly once.</summary>
    [Fact]
    public void PlanSplit_OnlyTheDominantBundleIsCut_AndTheRestLandOnce()
    {
        var plan = ShardPlanner.PlanSplit(
            Items(("erm", 900), ("scm", 100), ("misc", 100), ("upgrade", 100)), jobs: 4, minPieceWeight: 50);

        Assert.Equal(new[] { "erm" }, plan.SplitBundles.ToArray());
        var names = plan.Shards.SelectMany(s => s.Select(i => i.Name)).ToList();
        Assert.Equal(1, names.Count(n => n == "scm"));
        Assert.Equal(1, names.Count(n => n == "misc"));
        Assert.Equal(1, names.Count(n => n == "upgrade"));
        Assert.True(names.Count(n => n == "erm") > 1);
        // no worker holds two pieces of the same bundle: that would not be a split
        Assert.All(plan.Shards, s => Assert.Equal(s.Count, s.Select(i => i.Name).Distinct().Count()));
    }

    [Fact]
    public void PlanSplit_OneJob_OrZeroWeight_DoesNotSplit()
    {
        Assert.Empty(ShardPlanner.PlanSplit(Items(("big", 1000)), jobs: 1, minPieceWeight: 1).SplitBundles);
        Assert.Empty(ShardPlanner.PlanSplit(Items(("z", 0)), jobs: 4, minPieceWeight: 1).SplitBundles);
        Assert.Empty(ShardPlanner.PlanSplit(System.Array.Empty<(string, long)>(), jobs: 4, 1).Shards);
    }

    [Fact]
    public void PlanSplit_IsDeterministic()
    {
        var items = Items(("erm", 900), ("scm", 100), ("misc", 100));
        string Dump(ShardPlanner.SplitPlan p) =>
            string.Join("|", p.Shards.Select(s => string.Join(",", s.Select(i => $"{i.Name}:{i.Weight}"))));

        Assert.Equal(Dump(ShardPlanner.PlanSplit(items, 3, 50)), Dump(ShardPlanner.PlanSplit(items, 3, 50)));
    }
}
