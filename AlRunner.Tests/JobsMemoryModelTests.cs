// JobsMemoryModelTests — the default worker-memory model against the runs it was fitted to
// (#5216; docs/jobs-unit-claiming.md § Memory). The decision logic is pinned over an explicit
// model in JobsMemorySizingTests; this pins the CONSTANTS, so a change to them that stops
// describing the measurements fails here instead of silently moving every plan.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsMemoryModelTests
{
    /// <summary>Peak proportional set size of the whole process tree (the runner, its workers and the
    /// backup-reader sidecar), with --test-data, on BC 28.1.49838.53910, 12 cores, 15 GB. Tests per
    /// worker are what each worker's own summary printed. Measured with tools/process-tree-peak.py.</summary>
    public static IEnumerable<object[]> Measured() => new[]
    {
        new object[] { "Tests-ERM serial, random seed", new[] { 9497 }, 4875 },
        new object[] { "Tests-ERM serial, seed 7", new[] { 9497 }, 4975 },
        new object[] { "Tests-ERM --jobs 2", new[] { 4288, 5209 }, 6784 },
        new object[] { "Tests-ERM --jobs 3", new[] { 2762, 3759, 2976 }, 8188 },
        new object[] { "Tests-SMB serial, seed 7", new[] { 1027 }, 2295 },
        new object[] { "Tests-SMB --jobs 2", new[] { 436, 591 }, 3441 },
        new object[] { "Tests-SMB --jobs 3", new[] { 334, 241, 452 }, 4387 },
        new object[] { "Tests-VAT serial, seed 7", new[] { 1200 }, 2230 },
        new object[] { "Tests-Job serial, seed 7", new[] { 1290 }, 2608 },
        new object[] { "Tests-Workflow serial, seed 7", new[] { 1058 }, 2579 },
        new object[] { "Tests-ERM, one codeunit of 11 tests, warm", new[] { 11 }, 1453 },
        new object[] { "Tests-SMB, one codeunit of 4 tests, warm", new[] { 4 }, 1322 },
        new object[] { "Tests-VAT, one codeunit of 4 tests, warm", new[] { 4 }, 1385 },
        new object[] { "Tests-SCM, one codeunit of 6 tests, warm", new[] { 6 }, 1549 },
    };

    /// <summary>The model never sits more than 20% above a measured peak, and under it only far
    /// enough that a plan AT the budget still leaves room: sized exactly to the budget
    /// (<see cref="JobsMemory.Headroom"/> of the free memory), a run that really used
    /// <c>measured</c> would use <c>Headroom * measured / estimate</c> of the free memory, and that
    /// stays at or under 97%. The worst fitted run (Tests-Workflow, -16%) uses 96%. This is
    /// in-sample: the one run the first fit had not seen and missed by 21% would have used 101%, so
    /// the headroom covers the fitted runs and is an extrapolation beyond them, not a held-out margin.</summary>
    [Theory]
    [MemberData(nameof(Measured))]
    public void TheDefaultModel_KeepsAPlanAtItsBudgetUnder97PercentOfFreeMemory(string run, int[] testsPerWorker, int measuredMb)
    {
        var estimateMb = testsPerWorker.Sum(n => JobsMemory.Model.EstimateBytes(n)) / (1024.0 * 1024);

        Assert.True(estimateMb / measuredMb <= 1.20,
            $"{run}: estimated {estimateMb:F0} MB against a measured {measuredMb} MB, more than 20% over");
        var useAtTheBudget = JobsMemory.Headroom * measuredMb / estimateMb;
        Assert.True(useAtTheBudget <= 0.97,
            $"{run}: a plan at the budget would use {useAtTheBudget:P0} of free memory");
    }

    /// <summary>The shape the fit rests on: tests grow a worker's memory, but less than in
    /// proportion, so a worker that runs half of a bundle costs more than half of one that runs it
    /// all. A linear model would have the second of two workers cost nothing extra.</summary>
    [Fact]
    public void WorkerMemoryGrowsWithTests_ButLessThanInProportion()
    {
        var m = JobsMemory.Model;
        var all = m.EstimateBytes(9000);
        var half = m.EstimateBytes(4500);
        var none = m.EstimateBytes(0);

        Assert.True(none < half && half < all);
        Assert.True(half - none > (all - none) / 2, "half the tests must cost more than half the growth");
    }
}
