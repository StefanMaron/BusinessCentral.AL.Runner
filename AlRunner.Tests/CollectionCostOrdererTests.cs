// CollectionCostOrdererTests — pins the scheduling property that #1829 was opened to fix.
//
// The measurement (see CollectionCostOrderer's header for the full timeline) is that the
// suite's four worker threads sit at a flat 4.0/4 occupancy for the first two thirds of
// the run and then at exactly 1.0 for the last 187 s, because xUnit queued the single
// longest collection 237 s in. Every claim below is about that: the heaviest collections
// must be dispatched first, and the resulting packing must actually be tight.
//
// These are deliberately not "the orderer returns something" tests. Each asserts a
// concrete position or a concrete makespan bound that a pass-through orderer fails.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class CollectionCostOrdererTests
{
    /// <summary>
    /// Runs the orderer's real generic core over the display names xUnit v2 gives a class
    /// that declares no [Collection], and returns the bare class names in dispatch order.
    /// <see cref="CollectionCostOrderer.OrderTestCollections"/> is a one-line adapter onto
    /// this same core, so everything asserted here is asserted about the shipped path.
    /// </summary>
    private static List<string> Order(params string[] classNames) =>
        CollectionCostOrderer
            .HeaviestFirst(
                classNames.Select(n => "Test collection for AlRunner.Tests." + n).ToList(),
                displayName => displayName)
            .Select(d => d.Split('.')[^1])
            .ToList();

    /// <summary>
    /// The whole point. Fed the two measured-heaviest collections LAST — the adversarial
    /// input that actually happened in CI — the orderer must hand them back FIRST, because
    /// a collection is strictly serial and therefore caps the run at (its start + its
    /// duration). ServerCancelTests alone is 285 s; dispatching it 237 s in produced a
    /// 187 s single-threaded tail.
    /// </summary>
    [Fact]
    public void Order_HandsBackTheMeasuredHeaviestCollectionsFirst()
    {
        var ordered = Order(
            "CliDocumentationTests",
            "BundleRootValidationTests",
            "ServerTests",
            "ServerCancelTests",
            "CacheKeyDependencyClosureTests");

        // #1851/#1857 cut CacheKeyDependencyClosureTests from 292s to 196s, so
        // ServerCancelTests (285s, untouched by that fix) now outranks it — swapped
        // relative to the original #1829 measurement, and correctly so.
        Assert.Equal("ServerCancelTests", ordered[0]);
        Assert.Equal("CacheKeyDependencyClosureTests", ordered[1]);
        Assert.Equal("ServerTests", ordered[2]);
        // ...and the two unmeasured ones last, in their input order (see the stability test).
        Assert.Equal(new[] { "CliDocumentationTests", "BundleRootValidationTests" }, ordered.Skip(3));
    }

    /// <summary>
    /// A collection the table has never measured must NOT sink to the back: an unmeasured
    /// collection is the one whose tail risk is unknown, and the cheapest insurance is to
    /// start it early. It ranks above every collection measured below
    /// <see cref="CollectionCostOrderer.UnmeasuredWeightSeconds"/> s and below every
    /// collection measured above it — the two directions are asserted separately so a
    /// "put unknowns first" or "put unknowns last" regression fails one of them.
    /// </summary>
    [Fact]
    public void Order_UnmeasuredCollection_OutranksTheCheapButNotTheExpensive()
    {
        // TestTimeoutFlagTests measured 21 s, below the 30 s unmeasured weight.
        Assert.Equal(
            new[] { "SomeBrandNewSuiteTests", "TestTimeoutFlagTests" },
            Order("TestTimeoutFlagTests", "SomeBrandNewSuiteTests"));

        // ServerTests measured 81 s, above it.
        Assert.Equal(
            new[] { "ServerTests", "SomeBrandNewSuiteTests" },
            Order("SomeBrandNewSuiteTests", "ServerTests"));
    }

    /// <summary>
    /// Issue #1887: InstallSeedDepCompanyCacheTests (~196 s, added by #1867) and
    /// CountBaselineIntegrationTests (~84 s, added by #1882) were absent from the table,
    /// fell back to <see cref="CollectionCostOrderer.UnmeasuredWeightSeconds"/> (30 s), and
    /// were dispatched at t=383 s / t=400 s of a 581 s run — a single-threaded tail on
    /// every CI leg. This asserts the ordering claim directly (not wall clock, which is
    /// machine-dependent and would pass against a gutted implementation on a fast runner):
    /// both classes must now carry their measured weight and rank ahead of collections that
    /// are measured but genuinely lighter — never after them, which is what "absent from
    /// the table" used to mean in practice.
    /// </summary>
    [Fact]
    public void Order_PreviouslyUnmeasuredHeavyClasses_AreNotDispatchedAfterMeasuredLighterOnes()
    {
        // The weights themselves — a wrong or reverted table entry fails here directly,
        // before the ordering assertions below could mask it behind a coincidental sort.
        Assert.Equal(196, CollectionCostOrderer.WeightSeconds("InstallSeedDepCompanyCacheTests"));
        Assert.Equal(84, CollectionCostOrderer.WeightSeconds("CountBaselineIntegrationTests"));

        // Positive: InstallSeedDepCompanyCacheTests (196 s) must come before both
        // TestFilterFlagTests (99 s) and ServerTests (81 s) — measured, and lighter. Before
        // this fix it fell back to 30 s and landed AFTER both.
        Assert.Equal(
            new[] { "InstallSeedDepCompanyCacheTests", "TestFilterFlagTests", "ServerTests" },
            Order("TestFilterFlagTests", "ServerTests", "InstallSeedDepCompanyCacheTests"));

        // CountBaselineIntegrationTests (84 s) must outrank ServerTests (81 s) but NOT
        // TestFilterFlagTests (99 s) — it is genuinely lighter than that one, so a correct
        // fix places it honestly between them rather than promoting it straight to the front.
        Assert.Equal(
            new[] { "TestFilterFlagTests", "CountBaselineIntegrationTests", "ServerTests" },
            Order("ServerTests", "TestFilterFlagTests", "CountBaselineIntegrationTests"));
    }

    /// <summary>
    /// Negative companion to the test above: fixing the two unmeasured-heavy classes must
    /// not hoist an unrelated, genuinely light collection along with them.
    /// TestTimeoutFlagTests measured 21 s — the lightest entry in the table — and must stay
    /// behind both, proving the fix corrected two specific entries rather than flattening
    /// the ordering.
    /// </summary>
    [Fact]
    public void Order_GenuinelyLightMeasuredClass_IsNotHoistedByTheFix()
    {
        Assert.Equal(
            new[] { "InstallSeedDepCompanyCacheTests", "CountBaselineIntegrationTests", "TestTimeoutFlagTests" },
            Order("TestTimeoutFlagTests", "CountBaselineIntegrationTests", "InstallSeedDepCompanyCacheTests"));
    }

    /// <summary>
    /// Equal weights preserve input order. Without this the dispatch order of the ~66
    /// unmeasured collections would depend on sort implementation details, and a
    /// before/after wall-clock comparison would carry that noise.
    /// </summary>
    [Fact]
    public void Order_IsStable_AcrossCollectionsOfEqualWeight()
    {
        var input = new[] { "ZzzTests", "AaaTests", "MmmTests" };
        Assert.Equal(input, Order(input));
    }

    /// <summary>
    /// The order xUnit actually dispatched these 22 collections in during the measured run,
    /// taken from the TRX trace. ServerCancelTests — the second-heaviest at 285 s — is 17th.
    /// </summary>
    private static readonly string[] ObservedDispatchOrder =
    {
        "TestFilterFlagTests",
        "CrossBundleModuleIdentityDedupTests",
        "CacheKeyDependencyClosureTests",
        "ExpectationManifestWiringTests",
        "SourceDepCacheEnumMetadataTests",
        "TestPageDrillDownDispatchTests",
        "TestTimeoutFlagTests",
        "ServerTests",
        "EmitExclusionLoudnessTests",
        "LayeredCacheTests",
        "ServerStreamingTests",
        "TestIsolationMethodAliasTests",
        "OutputFormatTests",
        "BundleSuiteErrorLoudnessTests",
        "ServerTestIsolationTests",
        "SourceDepSymbolsWithoutPackageCacheTests",
        "ServerCancelTests",
        "PhaseLogIntegrationTests",
        "BcVersionFloorSkipTests",
        "BatchAppIdentityTests",
        "SuiteEnumerationTests",
        "DefineFlagIntegrationTests",
    };

    /// <summary>
    /// The load-bearing claim, checked against the measured weights and the order xUnit
    /// really used: greedy list scheduling of the orderer's output onto 4 threads must land
    /// within 10% of the unbeatable total/4 bound (382 s here), where the order it replaces
    /// simulates at 499 s. A pass-through orderer reproduces the 499 s and fails both
    /// assertions.
    /// </summary>
    [Fact]
    public void Order_PacksFourThreadsWithinTenPercentOfTheWorkDividedByFour()
    {
        var measured = CollectionCostOrderer.MeasuredWeightSeconds;
        // Subset, not exact-equality: ObservedDispatchOrder is a fixed historical trace
        // (#1829's original 22-collection measurement) and the table has legitimately grown
        // since (#1887 added two more heavy classes). The invariant this protects is that
        // every class the makespan math below indexes by is still in the table — not that
        // the table's size is frozen at 22.
        var missing = ObservedDispatchOrder.Where(c => !measured.ContainsKey(c)).ToList();
        Assert.Empty(missing);

        var bound = ObservedDispatchOrder.Sum(c => measured[c]) / 4.0;
        var orderedMakespan = Makespan(Order(ObservedDispatchOrder).Select(c => (double)measured[c]));
        var observedMakespan = Makespan(ObservedDispatchOrder.Select(c => (double)measured[c]));

        Assert.InRange(orderedMakespan, bound, bound * 1.10);
        Assert.True(
            observedMakespan > orderedMakespan * 1.15,
            $"observed order {observedMakespan:F0}s vs ordered {orderedMakespan:F0}s (bound {bound:F0}s) — "
            + "the ordering is not buying anything, so either the weights or the sort is wrong");
    }

    private static double Makespan(IEnumerable<double> durations, int threads = 4)
    {
        var busy = new double[threads];
        foreach (var d in durations)
        {
            var min = Array.IndexOf(busy, busy.Min());
            busy[min] += d;
        }

        return busy.Max();
    }

    /// <summary>
    /// Rot guard. Every name in the weight table must still resolve to a test class in this
    /// assembly. A renamed or deleted class would otherwise silently stop being prioritised
    /// and the tail would come back with nothing failing.
    /// </summary>
    [Fact]
    public void MeasuredWeights_NameOnlyTestClassesThatStillExist()
    {
        var testClasses = typeof(CollectionCostOrdererTests).Assembly
            .GetTypes()
            .Where(t => t.IsClass && t.GetMethods()
                .Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = CollectionCostOrderer.MeasuredWeightSeconds.Keys
            .Where(n => !testClasses.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
        Assert.True(
            CollectionCostOrderer.MeasuredWeightSeconds.Count >= 20,
            "the weight table has shrunk below the set of collections that can create a tail");
    }

    /// <summary>
    /// The orderer is dead code unless the assembly attribute names it. Asserts the exact
    /// type and assembly strings xUnit resolves, so a typo (which xUnit reports only as a
    /// diagnostic message and otherwise ignores) fails the build instead of silently
    /// restoring the old schedule.
    /// </summary>
    [Fact]
    public void Assembly_WiresTheOrderer()
    {
        // xUnit does not surface the attribute's ctor arguments as properties, so read the
        // metadata directly — those two strings are exactly what xUnit type-resolves.
        var data = CustomAttributeData.GetCustomAttributes(typeof(CollectionCostOrdererTests).Assembly)
            .Single(d => d.AttributeType == typeof(TestCollectionOrdererAttribute));
        Assert.Equal(
            new object?[] { typeof(CollectionCostOrderer).FullName, typeof(CollectionCostOrderer).Assembly.GetName().Name },
            data.ConstructorArguments.Select(a => a.Value).ToArray());
    }
}
