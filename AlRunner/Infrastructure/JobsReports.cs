// JobsReports — the run's `--out` and `--output-json` under a `--jobs` fan-out (#5129).
//
// Each worker used to write its own `--out` to the one path the caller named (the last to finish
// won) and print its own `--output-json` document, so the run's report held a fraction of it.
// Now the PARENT writes both, from the workers' full results: a worker given
// ShardResultsEnvVar writes what ResumeCarry carries (everything either report reads) and the
// parent folds the files with the merge a watchdog resume uses for its attempts, so a bundle
// several workers share is one bucket and its tests are the union of what each claimed.
// docs/jobs-unit-claiming.md § Reports.

namespace AlRunner.Infrastructure;

internal static class JobsReports
{
    /// <summary>Where a worker writes its whole results, for the parent to merge. Set by the
    /// parent only when the caller asked for --out or --output-json; unset, a worker writes
    /// nothing extra. A watchdog resume re-execs with the environment inherited, so the final
    /// attempt (the only one that reaches the output block) writes it.</summary>
    public const string ShardResultsEnvVar = "AL_RUNNER_JOBS_SHARD_RESULTS";

    public static string? ShardResultsPath()
        => Environment.GetEnvironmentVariable(ShardResultsEnvVar) is { Length: > 0 } p ? p : null;

    /// <summary>The run's buckets in the order the caller named the bundles, and the workers whose
    /// results could not be read. A worker with no readable file contributes one ExecuteFailed bucket
    /// per bundle it was handed, with `ProcessError` saying how it ended: a report that omitted them
    /// would read as a run that never had those bundles.</summary>
    public static (List<BucketResult> Buckets, List<int> LostShards) Merge(
        IReadOnlyList<string> resultFiles,
        IReadOnlyList<IReadOnlyList<string>> shardBundles,
        IReadOnlyList<string> bundleOrder,
        IReadOnlyList<string> howEachEnded)
    {
        var attempts = new List<IReadOnlyList<BucketResult>>();
        var lost = new List<int>();
        var synthetic = new List<(int Shard, List<BucketResult> Buckets)>();
        for (var i = 0; i < resultFiles.Count; i++)
        {
            var read = ResumeCarry.ReadAttempts(new[] { resultFiles[i] }, out _);
            if (read.Count == 1) { attempts.Add(read[0]); continue; }
            lost.Add(i);
            synthetic.Add((i, shardBundles[i].Select(b => new BucketResult(
                Path.GetFullPath(b), BucketStage.ExecuteFailed, Array.Empty<string>(),
                $"--jobs worker {i} ended ({howEachEnded[i]}) without handing back its results, so this "
                + "bundle's results are missing from this report",
                Array.Empty<TestResult>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)).ToList()));
        }

        var merged = ResumeCarry.MergeAttempts(attempts);
        merged.AddRange(synthetic.SelectMany(s => s.Buckets));

        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bundleOrder.Count; i++) position.TryAdd(ParallelFanOut.Normalize(bundleOrder[i]), i);
        int At(BucketResult b) => position.TryGetValue(ParallelFanOut.Normalize(b.BucketPath), out var p) ? p : int.MaxValue;
        // OrderBy is stable: a bundle's own buckets, and bundles the caller did not name, keep their order.
        return (merged.OrderBy(At).ToList(), lost);
    }

    /// <summary>The run's exit code once the reports are written. A worker whose results are missing leaves
    /// the report short, so the run is at least 2, ranked with a lost carried attempt (Program.cs
    /// `carryIncomplete`): a statement about the report, above a plain test failure and below a compile
    /// failure. A report that could not be written raises only a run that would have exited 0, as the
    /// single-process run does (docs/cli-output-paths.md), never one that already failed more specifically.</summary>
    public static int Escalate(int worst, int lostShards, int lostOutputs)
        => lostShards > 0 && worst < 2 ? 2
         : lostOutputs > 0 && worst == 0 ? 2
         : worst;
}
