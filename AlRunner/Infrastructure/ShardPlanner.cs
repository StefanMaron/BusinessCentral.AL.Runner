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
// There is a second, harder reason to shard at all, measured on those buckets: peak RSS is
// driven by how many BUNDLES a process loads, not by how many tests it runs. Measured on this
// machine: 3 bundles / 939 tests peaked at 4.4 GB while 1 bundle / 1,027 tests peaked at 3.7 GB,
// and running 10x the tests inside ONE bundle (106 -> 1,027) cost only +0.4 GB. Each bundle
// brings its own emitted assemblies, symbols and object metadata, none of which a test rollback
// owns or can release.
//
// Isolation is NOT the gap there, which is worth stating because it is the obvious suspect:
// per-test resets (1,027 of them) peaked within 1% of per-codeunit (44), and disabling resets
// entirely cost 33% MORE — so the rollback is doing its job on the state it actually owns.
//
// So all 33 BaseApp buckets do not fit in one process however the tests are counted, and
// splitting the BUNDLES across workers is what makes that run possible rather than merely
// faster. It also contains a hung test to its own shard instead of ending the whole run.
//
// (Peaks vary run to run by roughly 20% — the same bucket measured 3.1, 3.3 and 3.7 GB across
// repeats — so treat these as magnitudes, not constants.)
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
