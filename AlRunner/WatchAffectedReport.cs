// WatchAffectedReport — turns one `--watch --affected` cycle (a run through the server's
// affected selection, Program.RunTestsWithSelection) into the watch loop's report shapes: the
// BucketResults its per-test lines, summary and dashboard read, and the lines saying what the
// selection did. docs/watch-affected.md.
using AlRunner.Infrastructure;

namespace AlRunner;

internal static class WatchAffectedReport
{
    /// <summary>The marker every line this report prints starts with.</summary>
    public const string Tag = "[watch] affected:";

    /// <summary>Whether a line <see cref="Describe"/> returned is the environment warning (#5028).</summary>
    public static bool IsWarning(string line) => line.StartsWith(Tag + " WARNING:", StringComparison.Ordinal);

    /// <summary>
    /// One bucket per bundle when the run answered for each (it hands results back in the order
    /// it was given), otherwise one bucket holding everything, so no result or error is dropped.
    /// Only tests that ran are in a bucket: a skipped test is absent, never a pass.
    /// </summary>
    public static List<BucketResult> ToBuckets(IReadOnlyList<string> bundles, AffectedRunOutcome outcome,
        TimeSpan elapsed, IReadOnlyList<CompanyInitFailure>? companyInitFailures)
    {
        var buckets = new List<BucketResult>();
        var paired = outcome.Runs.Count == bundles.Count;
        var groups = paired
            ? outcome.Runs.Select((r, k) => (Path: Path.GetFullPath(bundles[k]), Runs: (IReadOnlyList<ServerRunResult>)new[] { r }))
            : new[] { (Path: bundles.Count > 0 ? Path.GetFullPath(bundles[0]) : "", Runs: outcome.Runs) };
        foreach (var (path, runs) in groups)
        {
            var tests = runs.SelectMany(r => r.Tests).ToList();
            var errors = runs.SelectMany(r => r.CompileErrors ?? Array.Empty<CompilationErrorGroup>())
                .SelectMany(g => g.Errors.Select(e => $"{g.File}: {e}"))
                .ToList();
            var stage = tests.Count == 0 && errors.Count > 0 ? BundleFailureStage.Classify(errors) : BucketStage.Ran;
            // The run reports no per-phase times, so the whole cycle is booked as run time.
            buckets.Add(new BucketResult(path, stage, errors, null, tests,
                TimeSpan.Zero, TimeSpan.Zero, buckets.Count == 0 ? elapsed : TimeSpan.Zero,
                RanGroupCount: tests.Count > 0 ? 1 : 0,
                CompanyInitFailures: buckets.Count == 0 ? companyInitFailures : null));
        }
        return buckets;
    }

    /// <summary>Discovered tests the run skipped whose last recorded result was not a pass.</summary>
    public static List<string> SkippedFailing(AffectedRunOutcome outcome, AffectedSelectionState state)
    {
        var names = new List<string>();
        foreach (var (bundle, discovered) in outcome.DiscoveredTestsByBundle)
        {
            if (!outcome.SelectedTestsByBundle.TryGetValue(bundle, out var selected) || selected == null) continue;
            state.FailingTestsByBundle.TryGetValue(bundle, out var failing);
            names.AddRange(discovered.Where(t => !selected.Contains(t) && (failing?.Contains(t) ?? false)));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// What the selection did this cycle. The first line always carries the three counts, so a
    /// narrowed cycle can never read as a full run; a forced full run says why.
    /// </summary>
    public static List<string> Describe(ServerSelection? selection, IReadOnlyList<string> skippedFailing)
    {
        if (selection == null)
            return new List<string> { $"{Tag} no selection was made (no bundle reached test execution)" };
        var failing = selection.SkippedFailing ?? 0;
        var unaffected = Math.Max(0, selection.Skipped - failing);
        var total = selection.Ran + selection.Skipped;
        var lines = new List<string>
        {
            $"{Tag} ran {selection.Ran} of {total}   skipped-unaffected {unaffected}   skipped-failing {failing}",
        };
        // #5028: a baseline from another environment was used; never a line that can go unnoticed.
        if (selection.EnvironmentDrift is { } drift)
            lines.Add($"{Tag} {AffectedEnvironmentDrift.Warning(drift)}");
        if (selection.ForcedFull)
            lines.Add($"{Tag} full run — {selection.Reason ?? "reason not recorded"}");
        else if (selection.ChangedObjects.Count > 0)
            lines.Add($"{Tag} changed: {string.Join(", ", selection.ChangedObjects)}");
        if (skippedFailing.Count > 0)
            lines.Add($"{Tag} not re-run, still failing from an earlier cycle: {string.Join(", ", skippedFailing)}");
        return lines;
    }
}
