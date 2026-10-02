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
// (Measured again with --test-data on Tests-ERM: a worker grows from 1.5 GB at 11 tests to
// 4.9 GB at 9,497, so tests run do cost memory there. docs/jobs-unit-claiming.md § Memory.)
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

    /// <summary>A shard plan in which a heavy bundle may appear in several shards (#5130).
    /// <see cref="SplitBundles"/> are the names that do: the workers sharing one claim every
    /// test codeunit of it first come, first served instead of each running the whole bundle.</summary>
    public sealed record SplitPlan(
        List<List<(string Name, long Weight)>> Shards, IReadOnlySet<string> SplitBundles,
        string? MemoryNote = null);

    /// <summary>
    /// <see cref="Plan"/>, except that a bundle heavier than one worker's fair share is cut into
    /// pieces that land on DIFFERENT shards. A bundle gets round(weight / (total / jobs)) pieces,
    /// at most <paramref name="jobs"/>, and never so many that a piece is lighter than
    /// <paramref name="minPieceWeight"/>, because every extra worker pays startup, bundle load and
    /// test-data company load again. When no bundle qualifies the result is exactly
    /// <c>Plan(items, jobs)</c> with an empty <see cref="SplitPlan.SplitBundles"/>.
    ///
    /// A piece is only a placement weight: nothing here knows which test codeunits it covers,
    /// the workers sharing the bundle decide that at run time.
    ///
    /// <paramref name="maxPieces"/> caps the pieces of the bundles it names (the memory limit,
    /// #5216): a bundle it does not name keeps what the rules above give, and one it names with 1
    /// or less is not split.
    /// </summary>
    public static SplitPlan PlanSplit(
        IReadOnlyList<(string Name, long Weight)> items, int jobs, long minPieceWeight,
        IReadOnlyDictionary<string, int>? maxPieces = null)
    {
        var noSplit = new SplitPlan(Plan(items, jobs), new HashSet<string>(StringComparer.Ordinal));
        if (jobs <= 1 || items.Count == 0) return noSplit;

        var total = items.Sum(i => Math.Max(0, i.Weight));
        if (total == 0) return noSplit;
        var fairShare = (double)total / jobs;

        var pieces = new List<(string Name, long Weight)>();
        var anySplit = false;
        foreach (var item in items)
        {
            var w = Math.Max(0, item.Weight);
            var k = (int)Math.Round(w / fairShare, MidpointRounding.AwayFromZero);
            k = Math.Min(k, jobs);
            if (minPieceWeight > 0) k = (int)Math.Min(k, w / minPieceWeight);
            if (maxPieces != null && maxPieces.TryGetValue(item.Name, out var cap)) k = Math.Min(k, cap);
            k = Math.Max(k, 1);
            if (k > 1) anySplit = true;
            for (var p = 0; p < k; p++) pieces.Add((item.Name, w / k));
        }
        if (!anySplit) return noSplit;

        var shardCount = Math.Min(jobs, pieces.Count);
        var shards = new List<List<(string Name, long Weight)>>();
        for (var i = 0; i < shardCount; i++) shards.Add(new List<(string, long)>());
        var load = new long[shardCount];

        foreach (var piece in pieces
                     .OrderByDescending(x => x.Weight)
                     .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            // Lightest shard that does not already hold a piece of this bundle: two pieces on one
            // worker would just be that worker claiming more of the bundle, which is not a split.
            var target = -1;
            for (var s = 0; s < shardCount; s++)
            {
                if (shards[s].Any(x => x.Name == piece.Name)) continue;
                if (target < 0 || load[s] < load[target]) target = s;
            }
            // k <= jobs and shardCount = min(jobs, pieces), so a free shard always exists.
            shards[target].Add(piece);
            load[target] += piece.Weight;
        }

        var split = new HashSet<string>(
            shards.SelectMany(s => s.Select(x => x.Name))
                  .GroupBy(n => n, StringComparer.Ordinal)
                  .Where(g => g.Count() > 1)
                  .Select(g => g.Key),
            StringComparer.Ordinal);
        return new SplitPlan(shards, split);
    }
}
