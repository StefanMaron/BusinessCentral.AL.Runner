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
// CacheKeyDependencyClosureTests has 2 and 292 s). The only honest input is measurement,
// so the numbers below are seconds observed in a real 4-way run, and
// MeasuredWeights_NameOnlyTestClassesThatStillExist fails the build if one of them stops
// naming a real class.
//
// Staleness is bounded by design rather than by discipline: a collection missing from the
// table is treated as UnmeasuredWeightSeconds (30 s), which ranks it above every measured
// collection below that and below the fifteen that are above it. A newly added slow class
// therefore starts in the first half of the run — never last — while the ~66 collections
// that genuinely cost milliseconds contribute 2.8 s in total and cannot displace anything
// that matters. Re-measure with scripts/trx-occupancy.py when the shape changes.
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
            ["CacheKeyDependencyClosureTests"] = 292,
            ["ServerCancelTests"] = 285,
            ["TestFilterFlagTests"] = 99,
            ["PhaseLogIntegrationTests"] = 85,
            ["ServerTests"] = 81,
            ["TestPageDrillDownDispatchTests"] = 75,
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
