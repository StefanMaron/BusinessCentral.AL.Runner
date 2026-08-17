// CollectionCostOrderer — dispatch the heaviest test collections first (issue #1829).
//
// The measurement
// ---------------
// #1818 gave every test class its own collection and set maxParallelThreads: 4. The phase
// log (#1826) then reported only 1.83x achieved concurrency on the BC 28.1 leg and the
// obvious readings — "the thread cap is wrong", "something holds a lock", "the classes are
// unevenly sized" — were all guesses. A TRX occupancy timeline of a full local run
// (568 tests, 522.3 s span, which reproduces CI's ratio at 1.84x) settles it:
//
//   t=  0..335s   occupancy 4.0 / 4      <- saturated; no lock, no cap problem
//   t=335..365s   ramp 4.0 -> 1.0
//   t=365..522s   occupancy 1.0 / 4      <- 157 s single-threaded
//
// Everything running in that last stretch belongs to ONE collection, ServerCancelTests:
// 7 tests, 284.6 s of strictly serial work, which xUnit dispatched at t=237.3 s. A
// collection cannot finish before start + duration, so dispatching the longest one 45% of
// the way in *guarantees* a tail no thread budget can absorb. The second-heaviest,
// CacheKeyDependencyClosureTests at 292.0 s, happened to be dispatched at t=0.3 s and cost
// nothing.
//
// So the 1.83x figure was also misleading in its own right: it is (summed subprocess wall)
// / (step wall), and a lot of each test's time is host-side work outside the subprocess.
// Real thread occupancy was 3.00x. The recoverable loss is the tail, ~130 s per leg.
//
// The fix
// -------
// xUnit v2 queues collections onto its MaxConcurrencySyncContext in the order
// ITestCollectionOrderer returns them, so returning them longest-first is textbook LPT
// list scheduling: makespan <= (4/3) x optimum, and on these weights it simulates at
// 398.7 s against an unbeatable total/4 bound of 391.9 s.
//
// Two things this deliberately does NOT do:
//
//   * It does not raise maxParallelThreads. Occupancy is a flat 4.0 for the first two
//     thirds of the run, so the cap is not what is binding, and peak RSS per spawn tops out
//     at 3078 MiB on a 16 GB runner — a fifth concurrent heavy spawn is a memory risk with
//     no measured upside.
//   * It does not reorder the DisableParallelization collections (BcEngineCollection,
//     RecordPatchesSerialCollection). xUnit runs those serially AFTER every parallel
//     collection regardless of this orderer — confirmed in the same trace, where all of
//     them start at t=521.9 s. They total 0.4 s here, so they are not the problem, but no
//     ordering can move them.
//
// Why a measured table and not something automatic
// ------------------------------------------------
// ITestCollectionOrderer is handed collection identities only — no durations, no test
// counts, nothing that correlates (TestFilterFlagTests has 8 tests and 99 s;
// CacheKeyDependencyClosureTests has 2 and 196 s). The only honest input is measurement,
// so the numbers below are seconds observed in a real 4-way run, and
// MeasuredWeights_NameOnlyTestClassesThatStillExist fails the build if one of them stops
// naming a real class.
//
// #1887 corrected an over-optimistic claim that used to live in this paragraph: staleness
// is NOT bounded to "never last". A collection missing from the table is weighted
// UnmeasuredWeightSeconds (30 s), which ranks it below every measured collection above
// that — and by the time the suite has ~20 measured entries above 30 s, "below all of
// them" can land past the two-thirds mark of the run, not "the first half". That is
// exactly what happened: InstallSeedDepCompanyCacheTests (196 s, added by #1867) and
// CountBaselineIntegrationTests (84 s, added by #1882) both went unmeasured and were
// dispatched at t=383 s and t=400 s of a 581 s run — a tail on every leg, silently, until
// someone read a TRX occupancy report by hand.
//
// scripts/check-collection-weights.py is the loud guard that replaces "someone reads it by
// hand": run against the same trx/unit-tests.trx the occupancy report already parses, it
// fails CI when a collection above 2x UnmeasuredWeightSeconds is absent from this table —
// the exact shape of both misses above. It deliberately does NOT flag drift on entries
// that already exist (see its own header for why: the same class's summed duration varies
// materially by BC leg, and a percentage-drift check on top of that would be a noisy gate
// nobody trusts). Re-measure existing entries with scripts/trx-occupancy.py by hand when
// the shape changes; the guard's job is only to stop a class from going unmeasured
// forever.
using Xunit;
using Xunit.Abstractions;

[assembly: TestCollectionOrderer("AlRunner.Tests.CollectionCostOrderer", "AlRunner.Tests")]

namespace AlRunner.Tests;

/// <summary>
/// Orders test collections heaviest-measured-first so the longest strictly-serial
/// collection is never dispatched late enough to become a single-threaded tail.
/// </summary>
public sealed class CollectionCostOrderer : ITestCollectionOrderer
{
    /// <summary>
    /// Weight for a collection absent from <see cref="MeasuredWeightSeconds"/>. Ranks it
    /// above everything measured below 30 s and below everything measured above it — see
    /// the "Why a measured table" note in the file header.
    /// </summary>
    public const int UnmeasuredWeightSeconds = 30;

    /// <summary>
    /// Seconds of serial work per collection, from a full 4-way run of the suite
    /// (568 tests / 522.3 s span). Only collections at or above 20 s are listed; the
    /// remaining ~66 total 2.8 s and cannot create a tail. Keys are bare class names, which
    /// is what the implicit collection display name ends with.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> MeasuredWeightSeconds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // perf/boot-overhead: added with the on-disk install-baseline tier. Measured
            // 125.6s on the first CI run of that branch (BC 28.4 leg), where it was absent
            // from this table, fell back to UnmeasuredWeightSeconds and was dispatched at
            // t=181s of a 309s run — a 77s single-threaded tail, the #1887 pattern again.
            ["InstallBaselineDiskCacheTests"] = 125,
            ["ServerCancelTests"] = 285,
            // perf/boot-overhead: 37.8s measured on the same run; below the 60s freshness
            // threshold, listed so it is dispatched by measured cost, not by the fallback.
            ["EventSubscriberScanEquivalenceTests"] = 37,
            // #1851/#1857 cut this from 292s to 196s (--print-cache-key skips the four cold
            // AL compiles the class used to pay for). #1887 caught the table still saying
            // 292 — harmless for ordering (it already ranked at the top either way), but it
            // is the same silent-drift failure mode as the two entries below, just in the
            // direction that costs nothing instead of the direction that costs a tail.
            ["CacheKeyDependencyClosureTests"] = 196,
            // #1887: added by #1867, absent from this table since — fell back to
            // UnmeasuredWeightSeconds (30s) and got dispatched at t=383s of a 581s run,
            // producing a 73s single-threaded tail (it is 3rd-heaviest at ~196s of serial
            // work; see the file header and issue #1887 for the measured timeline).
            ["InstallSeedDepCompanyCacheTests"] = 196,
            ["TestFilterFlagTests"] = 99,
            ["PhaseLogIntegrationTests"] = 85,
            // #1922: 6 tests, each spawning a real runner subprocess against an AL fixture
            // (--coverage twice, --output-junit twice, table-trigger fixture once).
            ["CoverageTests"] = 85,
            // #1887: added by #1882 (--count-baseline), absent from this table since — same
            // fallback-to-30 failure as InstallSeedDepCompanyCacheTests above, dispatched at
            // t=400s.
            ["CountBaselineIntegrationTests"] = 84,
            ["ServerTests"] = 81,
            ["TestPageDrillDownDispatchTests"] = 75,
            // #1870: one test, spawning a real runner subprocess against an AL fixture
            // (BC engine cold-start + AL emit/compile once). Measured 63.9s in CI.
            ["TestPageBooleanRecBoundDispatchTests"] = 63,
            ["ServerTestIsolationTests"] = 69,
            ["ServerStreamingTests"] = 50,
            ["ExpectationManifestWiringTests"] = 47,
            ["LayeredCacheTests"] = 46,
            ["TestIsolationMethodAliasTests"] = 45,
            ["BatchAppIdentityTests"] = 42,
            ["SourceDepCacheEnumMetadataTests"] = 41,
            ["DefineFlagIntegrationTests"] = 41,
            ["SuiteEnumerationTests"] = 36,
            ["EmitExclusionLoudnessTests"] = 33,
            ["BundleSuiteErrorLoudnessTests"] = 32,
            ["BcVersionFloorSkipTests"] = 32,
            ["OutputFormatTests"] = 31,
            ["CrossBundleModuleIdentityDedupTests"] = 23,
            ["SourceDepSymbolsWithoutPackageCacheTests"] = 23,
            ["TestTimeoutFlagTests"] = 21,
        };

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        HeaviestFirst(testCollections, c => c.DisplayName);

    /// <summary>
    /// Stable descending sort by measured weight. Stability matters: it keeps the dispatch
    /// order of the many equal-weight collections deterministic, so a before/after wall
    /// clock measures the ordering change and not sort noise.
    /// </summary>
    public static IEnumerable<T> HeaviestFirst<T>(IEnumerable<T> items, Func<T, string> displayName) =>
        items
            .Select((item, index) => (item, index))
            .OrderByDescending(t => WeightSeconds(displayName(t.item)))
            .ThenBy(t => t.index)
            .Select(t => t.item)
            .ToList();

    /// <summary>
    /// Weight for a collection display name. xUnit v2 names an implicit collection
    /// "Test collection for &lt;full type name&gt;"; a [CollectionDefinition] one is named by
    /// its own string. Both are matched, so moving a class into a named collection later
    /// does not silently drop its weight.
    /// </summary>
    public static int WeightSeconds(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return UnmeasuredWeightSeconds;
        if (MeasuredWeightSeconds.TryGetValue(displayName, out var direct)) return direct;

        var lastToken = displayName[(displayName.LastIndexOf(' ') + 1)..];
        var bareName = lastToken[(lastToken.LastIndexOf('.') + 1)..];
        return MeasuredWeightSeconds.TryGetValue(bareName, out var measured)
            ? measured
            : UnmeasuredWeightSeconds;
    }
}
