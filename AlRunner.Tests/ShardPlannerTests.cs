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
}
