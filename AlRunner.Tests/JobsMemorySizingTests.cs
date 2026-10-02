// JobsMemorySizingTests — free memory limits how many workers share one bundle (#5216, on #5130).
// A worker costs a base before it runs anything, so each extra worker of a shared bundle costs that
// base again; the tests it runs are the bundle's tests shared out, not more of them. The model's
// constants are a measurement (docs/jobs-unit-claiming.md, § Memory), so these tests pin the
// DECISION over an explicit model and reading, not the numbers.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsMemorySizingTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("jobsmem-");

    public JobsMemorySizingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const long Mb = 1024 * 1024;

    /// <summary>A worker costs 1,000 MB to start and 1 MB per test it runs. A 400-file bundle of
    /// 10 tests a file is 4,000 tests: one worker running all of it costs 5,000 MB, and four
    /// sharing it cost 4 x 1,000 + 4,000 = 8,000 MB. 400 files is what lets four workers share a
    /// bundle at the default floor of 20 files a piece.</summary>
    private static readonly JobsMemory.WorkerModel Model = new(BaseMb: 1000, CoeffMb: 1.0, Exponent: 1.0);

    private string Bundle(string name, int files, int testsPerFile = 10)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var body = string.Concat(Enumerable.Repeat("    [Test]\n    procedure T()\n    begin\n    end;\n", testsPerFile));
        for (var i = 0; i < files; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.al"), body);
        return dir;
    }

    private static int Sharing(ShardPlanner.SplitPlan plan, string bundle)
        => plan.Shards.Count(s => s.Any(x => x.Name == bundle));

    // ── reading free memory ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseMemAvailableBytes_ReadsTheKilobyteFigure()
    {
        var meminfo = "MemTotal:       16057600 kB\nMemFree:         1000 kB\nMemAvailable:    5432100 kB\nBuffers: 1 kB\n";

        Assert.Equal(5432100L * 1024, JobsMemory.ParseMemAvailableBytes(meminfo));
    }

    /// <summary>A reading that is missing or garbled is unknown, never zero: zero would read as "no
    /// memory at all" and unknown as "plenty", and neither is what was measured.</summary>
    [Theory]
    [InlineData("MemTotal: 1 kB\n")]
    [InlineData("MemAvailable: lots kB\n")]
    [InlineData("")]
    public void ParseMemAvailableBytes_WhenAbsentOrGarbled_IsNull(string meminfo)
        => Assert.Null(JobsMemory.ParseMemAvailableBytes(meminfo));

    [Fact]
    public void CgroupRemainingBytes_IsTheLimitMinusTheUse_AndNullWhenThereIsNoLimit()
    {
        Assert.Equal(600L, JobsMemory.CgroupRemainingBytes("1000\n", "400\n"));
        Assert.Equal(0L, JobsMemory.CgroupRemainingBytes("1000", "1500"));
        Assert.Null(JobsMemory.CgroupRemainingBytes("max\n", "400\n"));
        Assert.Null(JobsMemory.CgroupRemainingBytes(null, "400"));
        Assert.Null(JobsMemory.CgroupRemainingBytes("1000", "oops"));
    }

    /// <summary>A limit on any ancestor binds the worker, so every directory up to the root is read.</summary>
    [Fact]
    public void CgroupDirectories_ListsThisCgroupAndEveryAncestor_DeepestFirst()
    {
        var dirs = JobsMemory.CgroupDirectories("0::/user.slice/user-1000.slice/app.scope\n", "/cg");

        Assert.Equal(new[]
        {
            "/cg/user.slice/user-1000.slice/app.scope", "/cg/user.slice/user-1000.slice", "/cg/user.slice", "/cg",
        }, dirs);
        Assert.Equal(new[] { "/cg" }, JobsMemory.CgroupDirectories("0::/\n", "/cg"));
        // cgroup v1 has no "0::" line: nothing to read, which is unknown rather than unlimited
        Assert.Empty(JobsMemory.CgroupDirectories("12:memory:/foo\n11:cpu:/foo\n", "/cg"));
    }

    // ── counting what a worker will run ─────────────────────────────────────────────────────

    [Fact]
    public void CountTests_CountsTheTestAttributesOfEveryAlFile()
    {
        var b = Bundle("b", files: 3, testsPerFile: 4);
        File.WriteAllText(Path.Combine(b, "not-al.txt"), "[Test]\n[Test]\n");
        File.WriteAllText(Path.Combine(b, "Other.al"), "    [Test]\n    [TEST]\n    [HandlerFunctions('H')]\n    [Test, X]\n");

        Assert.Equal(3 * 4 + 2, ParallelFanOut.CountTests(b));
    }

    // ── how heavy a bundle must be before it is shared at all ────────────────────────────────

    /// <summary>The default floor, from a measurement: Tests-SMB (45 files) ran 1.6x faster on two
    /// workers (22-file pieces) and barely faster again on three (15-file pieces), so a 45-file
    /// bundle is shared by two and not by three, and one a little under twice the floor is not
    /// shared at all.</summary>
    [Fact]
    public void TheDefaultFloor_SharesAFortyFiveFileBundleByTwoWorkers_NotThree_AndALighterOneNotAtAll()
    {
        Assert.Equal(20, ParallelFanOut.DefaultMinSplitFiles);
        var smb = Bundle("smb", 45);
        var lighter = Bundle("lighter", 39);

        Assert.Equal(2, Sharing(ParallelFanOut.PlanBundles(new[] { smb }, 2, null, null, Model), smb));
        Assert.Equal(2, Sharing(ParallelFanOut.PlanBundles(new[] { smb }, 3, null, null, Model), smb));
        Assert.Empty(ParallelFanOut.PlanBundles(new[] { lighter }, 2, null, null, Model).SplitBundles);
    }

    // ── what the plan does with it ──────────────────────────────────────────────────────────

    [Fact]
    public void WhenEveryWorkerFits_TheBundleIsSharedByAsManyAsTheSplitRulesGive()
    {
        var erm = Bundle("erm", 400);

        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 4, null, 100_000 * Mb, Model);

        Assert.Equal(4, Sharing(plan, erm));
        Assert.Null(plan.MemoryNote);
    }

    /// <summary>Four workers need 8,000 MB, three about 7,000, two 6,000. 8,000 MB free at 80%
    /// headroom is 6,400, which holds two.</summary>
    [Fact]
    public void WhenOnlyTwoWorkersFit_TheBundleIsSharedByTwo_AndTheNoteSaysWhy()
    {
        var erm = Bundle("erm", 400);

        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 4, null, 8_000 * Mb, Model);

        Assert.Equal(2, Sharing(plan, erm));
        Assert.Contains(erm, plan.SplitBundles);
        Assert.NotNull(plan.MemoryNote);
        Assert.Contains("by 2 worker(s), not 4", plan.MemoryNote);
        Assert.Contains("7.8 GB free", plan.MemoryNote);
        Assert.Contains(JobsMemory.FreeMemoryEnvVar, plan.MemoryNote);
    }

    /// <summary>Not even two workers fit (6,000 MB against 5,000 x 0.8 = 4,000): nothing is shared,
    /// and a lone bundle is one shard, as it was before sharing existed.</summary>
    [Fact]
    public void WhenNotEvenTwoFit_NothingIsShared()
    {
        var erm = Bundle("erm", 400);

        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 4, null, 5_000 * Mb, Model);

        Assert.Empty(plan.SplitBundles);
        Assert.Single(plan.Shards);
        Assert.Contains("by 1 worker(s), not 4", plan.MemoryNote);
    }

    /// <summary>An unreadable reading sizes nothing: it is neither plenty nor none, so the plan is
    /// exactly the one the split rules give.</summary>
    [Fact]
    public void AnUnknownReading_LeavesThePlanAlone()
    {
        var erm = Bundle("erm", 400);

        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 4, null, null, Model);

        Assert.Equal(4, Sharing(plan, erm));
        Assert.Null(plan.MemoryNote);
    }

    /// <summary>A bundle whose tests cannot be read has an unknown cost, and sizing from "no tests
    /// counted" would call the plan cheap exactly when it could not be measured: the plan is left
    /// as the split rules made it, not shrunk and not blessed with a note.</summary>
    [SkippableFact]
    public void ABundleWhoseTestsCannotBeRead_IsNotSizedFrom()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "needs POSIX file modes to make a file unreadable");
        var erm = Bundle("erm", 400);
        var hidden = Path.Combine(erm, "f0.al");
        File.SetUnixFileMode(hidden, UnixFileMode.None);
        Skip.If(CanRead(hidden), "running as a user that reads any file");

        Assert.Null(ParallelFanOut.CountTests(erm));
        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 4, null, 1 * Mb, Model);

        Assert.Equal(4, Sharing(plan, erm));
        Assert.Null(plan.MemoryNote);
    }

    private static bool CanRead(string path)
    {
        try { File.ReadAllText(path); return true; } catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Only SHARING is limited. Two light bundles on `--jobs 2` are two workers however
    /// little is free: how many workers an unshared run uses stays the caller's `--jobs`.</summary>
    [Fact]
    public void UnsharedBundles_KeepTheCallersJobCount_WhateverIsFree()
    {
        var a = Bundle("a", 20);
        var b = Bundle("b", 20);

        var plan = ParallelFanOut.PlanBundles(new[] { a, b }, 2, null, 1 * Mb, Model);

        Assert.Equal(2, plan.Shards.Count);
        Assert.Empty(plan.SplitBundles);
        Assert.Null(plan.MemoryNote);
    }

    /// <summary>A piece runs its SHARE of the bundle's tests, not all of them: two workers on the
    /// 4,000-test bundle cost 2 x (1,000 + 2,000) = 6,000 MB, and not 2 x 5,000, which is what a
    /// model that charged every worker the whole bundle would say.</summary>
    [Fact]
    public void AWorkerIsChargedItsShareOfTheTests_NotAllOfThem()
    {
        var erm = Bundle("erm", 400);
        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 2, null, 100_000 * Mb, Model);
        Assert.Equal(2, Sharing(plan, erm));

        var cost = ParallelFanOut.EstimateBytes(plan,
            new Dictionary<string, long> { [erm] = 400 }, new Dictionary<string, long> { [erm] = 4_000 }, Model);

        Assert.Equal(6_000 * Mb, cost);
    }

    /// <summary>The base is what sharing costs: a bundle of no tests still pays it on every extra
    /// worker, so free memory that holds one worker and a bit holds no second one.</summary>
    [Fact]
    public void ABundleOfNoTests_StillCostsABasePerWorker()
    {
        var empty = Bundle("empty", 400, testsPerFile: 0);

        // two workers = 2,000 MB; 2,400 MB x 0.8 = 1,920 does not hold them, 2,600 x 0.8 = 2,080 does
        Assert.Empty(ParallelFanOut.PlanBundles(new[] { empty }, 2, null, 2_400 * Mb, Model).SplitBundles);
        Assert.Equal(2, Sharing(ParallelFanOut.PlanBundles(new[] { empty }, 2, null, 2_600 * Mb, Model), empty));
    }

    /// <summary>Where the memory goes first: the bundle shared by the most workers loses one, since
    /// that is the smallest slowdown per worker given up. The other keeps its split. Five jobs:
    /// three pieces of one bundle and two of the other are five workers, and four once the first
    /// gives one up.</summary>
    [Fact]
    public void TheBundleWithTheMostWorkers_LosesOneFirst()
    {
        var big = Bundle("big", 500);
        var mid = Bundle("mid", 300);
        var plan = ParallelFanOut.PlanBundles(new[] { big, mid }, 5, null, 100_000 * Mb, Model);
        var bigBefore = Sharing(plan, big);
        var midBefore = Sharing(plan, mid);
        Assert.Equal((3, 2), (bigBefore, midBefore));

        // room for exactly one worker fewer than the unconstrained plan needs
        var need = ParallelFanOut.EstimateBytes(plan,
            new Dictionary<string, long> { [big] = 500, [mid] = 300 },
            new Dictionary<string, long> { [big] = 5_000, [mid] = 3_000 }, Model);
        var squeezed = ParallelFanOut.PlanBundles(new[] { big, mid }, 5, null, (long)(need / 0.8) - Mb, Model);

        Assert.Equal(bigBefore - 1, Sharing(squeezed, big));
        Assert.Equal(midBefore, Sharing(squeezed, mid));
    }

    [Fact]
    public void ARefusedRun_IsNeverSplit_ForAnyReading()
    {
        var erm = Bundle("erm", 400);

        var plan = ParallelFanOut.PlanBundles(new[] { erm }, 4, "reason", 100_000 * Mb, Model);

        Assert.Empty(plan.SplitBundles);
        Assert.Null(plan.MemoryNote);
    }
}
