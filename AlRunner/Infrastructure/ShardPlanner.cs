// ShardPlanner — split a run's bundles across `--jobs` worker processes (issue #2280).
//
// Why process-level and not threads: #2280 measured that in-process parallelism means auditing
// roughly 510 statics under AlRunner/Patches/, where a single missed one contaminates another
// worker's rows mid-run and presents as flakiness rather than as a failure. With the per-process
// boot tax measured at about 3 s, sharding across processes gets close to the full theoretical
// speedup without touching any of that shared state.
//
// Why balance by weight rather than one bundle per worker: Microsoft's BaseApp buckets span two
// orders of magnitude (Tests-Upgrade 11 tests, Tests-ERM 9,500). An equal COUNT of bundles per
// worker leaves one worker running ERM while the rest idle, and a run takes as long as its
// longest shard.
//
// There is a second, harder reason to shard at all, measured on those buckets: peak RSS tracks
// the number of tests a process has EXECUTED, not the loaded floor — 1.3 GB at 11 tests, 5.3 GB
// at 1,133, 7.9 GB at 2,859. Extrapolated, one process cannot finish all ~40,000 of them on an
// ordinary machine. Sharding bounds each worker's peak by its own test count, so it is a
// prerequisite for the full run rather than only an optimization. It also contains a hung test
// to its own shard instead of ending the whole run.
//
// Longest-processing-time assignment (heaviest first, always onto the currently lightest shard)
// is the standard greedy bound for this. Determinism is deliberate: a plan that reshuffles
// between runs makes a per-shard timing regression unreadable, so ties break on the item's own
// name, never on input order or hash iteration order.

namespace AlRunner.Infrastructure;

internal static class ShardPlanner
{
    /// <summary>
    /// Split <paramref name="items"/> into at most <paramref name="jobs"/> shards of roughly
    /// equal total weight. Never returns an empty shard — an empty one would spawn a worker that
    /// pays the full BC boot cost to run nothing — so the result has
    /// <c>min(jobs, items.Count)</c> shards.
    ///
    /// <paramref name="jobs"/> of 1 or less returns a single shard in the ORIGINAL order, so
    /// `--jobs 1` is byte-for-byte today's behaviour rather than a second code path that happens
    /// to agree.
    /// </summary>
    public static List<List<(string Name, long Weight)>> Plan(
        IReadOnlyList<(string Name, long Weight)> items, int jobs)
    {
        var result = new List<List<(string Name, long Weight)>>();
        if (items.Count == 0) return result;

        if (jobs <= 1)
        {
            result.Add(items.ToList());
            return result;
        }

        var shardCount = Math.Min(jobs, items.Count);
        for (var i = 0; i < shardCount; i++) result.Add(new List<(string, long)>());
        var load = new long[shardCount];

        // Heaviest first; ties by name so the plan does not depend on input order.
        var ordered = items
            .OrderByDescending(i => i.Weight)
            .ThenBy(i => i.Name, StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            // Lightest shard; ties by lowest index, so this is deterministic too.
            var target = 0;
            for (var s = 1; s < shardCount; s++)
                if (load[s] < load[target]) target = s;

            result[target].Add(item);
            load[target] += Math.Max(0, item.Weight);
        }

        return result;
    }
}
