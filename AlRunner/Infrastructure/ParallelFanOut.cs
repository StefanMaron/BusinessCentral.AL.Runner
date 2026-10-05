// ParallelFanOut — build the worker command line for `--jobs` (issue #2280).
//
// The parent re-invokes its own executable once per shard. Arg rewriting is where a fan-out
// quietly goes wrong: drop a flag and every worker runs a DIFFERENT configuration from the one
// the user asked for, so the aggregate number is not measuring what its caller believes.
//
// Three things this has to get right, each pinned by a test:
//   * every flag the user passed survives (a worker that loses --test-data reports a much lower
//     pass count, and nothing says why),
//   * `--jobs` does NOT survive, or each worker fans out again — a process bomb, not a run,
//   * only this shard's bundles are RUN, or every worker runs everything and the aggregate
//     double-counts. A worker is also handed the listed folders its bundles depend on, marked
//     dependency-only so it compiles and does not run them (JobsSourceDependencies, #5267).
//
// Telling a positional bundle path from a flag's VALUE needs to know which flags take one:
// `--cache /b/two` must keep its value even when /b/two is also a bundle root elsewhere in the
// run. ValueTakingFlags is that list, and ParallelFanOutFlagDriftTests fails when Program.cs
// grows a value-taking flag that is not in it — the alternative is a value silently eaten as a
// bundle path, which is exactly the class of bug this file exists to avoid.

namespace AlRunner.Infrastructure;

internal static class ParallelFanOut
{
    /// <summary>
    /// Flags that consume the NEXT argument as their value. Kept in sync with Program.cs's own
    /// parsing by ParallelFanOutFlagDriftTests.
    /// </summary>
    public static readonly IReadOnlySet<string> ValueTakingFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--artifact-path", "--bc-version", "--cache", "--count-baseline", "--count-out",
        "--country",
        "--coverage-out", "--define", "--dump-csharp", "--expectations", "--filter",
        "--isolation", "--out", "--output-junit", "--package-cache", "--preprocessor-symbols",
        "--resolve-version", "--test", "--test-data-company", "--test-isolation",
        "--test-timeout", "--jobs", "--seed",
        // Both take a value and both must reach a worker: a shard that lost --exclude-test would
        // walk straight back into the hang the parent already excluded, and one that lost
        // --resume-aborts would fall back to the default budget and start its own resume chain.
        "--exclude-test", "--resume-aborts", "--merge-counts", "--merge-results",
    };
    // `--count-out` is listed above so its PATH is read as a value rather than as a bundle
    // directory -- the drift this class exists to prevent, and the one that flag walked into
    // on its first CI run (run 34415016792). It never actually reaches a worker: Program.cs
    // REFUSES `--count-out` together with a fan-out, because each worker would write its own
    // shard's counts to the one path the caller named and the last one to finish would
    // silently become the run's count. See the refusal there for why that rather than an
    // aggregation.

    /// <summary>
    /// The command line for one worker: the parent's own arguments, with the bundle positionals
    /// replaced by <paramref name="shardBundles"/>, `--jobs` removed, `--output-junit`
    /// forced to <paramref name="junitPath"/> so the parent can aggregate, and `--out` /
    /// `--output-json` removed (the parent writes those, #5129).
    /// </summary>
    /// <param name="originalArgs">The parent's argv.</param>
    /// <param name="shardBundles">This worker's bundle dirs.</param>
    /// <param name="bundleRoots">Every bundle dir in the whole run — used to recognise which
    /// positionals are bundles, so an unrelated positional is left alone.</param>
    /// <param name="junitPath">Where this worker writes its JUnit XML.</param>
    public static List<string> BuildChildArgs(
        IReadOnlyList<string> originalArgs,
        IReadOnlyList<string> shardBundles,
        IReadOnlyList<string> bundleRoots,
        string junitPath)
    {
        var roots = new HashSet<string>(bundleRoots.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var child = new List<string>();
        var sawOut = false;

        for (var i = 0; i < originalArgs.Count; i++)
        {
            var a = originalArgs[i];

            if (a == "--jobs" || a == "-j")
            {
                if (i + 1 < originalArgs.Count) i++; // drop its value too
                continue;
            }

            // Replaced wholesale below, so the user's own path must not also survive: two
            // --output-junit values leave the last one winning, and the parent then reads a file
            // no worker wrote (or the user's report holds a single shard's results).
            if (a == "--output-junit")
            {
                if (i + 1 < originalArgs.Count) i++;
                continue;
            }

            // #5129: the caller's --out and --output-json describe the RUN, so the parent writes them
            // from the workers' results (JobsReports). Left on a worker, every one would write the
            // one --out path (the last to finish wins) and print its own document. --out still turns
            // the worker's FAILURE CLASSIFICATION block on, as --classify does without a path.
            if (a == "--out")
            {
                if (i + 1 < originalArgs.Count) i++;
                sawOut = true;
                continue;
            }
            if (a == "--output-json") continue;
            // The same reason: a worker forced to exit 0 hides the shard's verdict from the parent,
            // whose `Result:` line and `--output-json` exitCode would then say a failing run passed.
            // The parent applies it to the run's own code (StrictExit).
            if (a == "--no-strict-exit") continue;

            // Carried totals belong to the RUN, not to each worker. The parent aggregates every
            // shard's JUnit itself, so handing --merge-counts to all six workers would add the
            // same earlier-attempt totals six times and report a number larger than the tests
            // that exist. Listed in ValueTakingFlags above so its value is still recognised as a
            // value rather than read as a bundle path; dropped here so it reaches no worker.
            //
            // The OTHER direction is not this code's job and is not affected by the strip: a
            // worker that resumes itself builds its own --merge-counts chain (AbortResume) and
            // writes the carried cases into the JUnit it hands back (#2716), so Run() reading
            // that one file per shard sees everything the worker's attempts ran.
            if (a == "--merge-counts")
            {
                if (i + 1 < originalArgs.Count) i++;
                continue;
            }

            if (ValueTakingFlags.Contains(a))
            {
                child.Add(a);
                if (i + 1 < originalArgs.Count) child.Add(originalArgs[++i]);
                continue;
            }

            // A positional that names a bundle in this run belongs to whichever shard owns it,
            // and is re-added below. Anything else — another flag, or a positional this code does
            // not own — passes through untouched.
            if (!a.StartsWith("-", StringComparison.Ordinal) && roots.Contains(Normalize(a)))
                continue;

            child.Add(a);
        }

        if (sawOut) child.Add("--classify");
        child.AddRange(shardBundles);
        child.Add("--output-junit");
        child.Add(junitPath);
        return child;
    }


    /// <summary>
    /// Weight for shard planning: how many AL files a bundle holds. A proxy for how long it
    /// takes, available BEFORE anything is compiled — the real answer (test count) is only known
    /// after the compile the shard plan has to precede. Measured across Microsoft's BaseApp
    /// buckets the two track closely enough to keep the heaviest bundles off one worker, which
    /// is all the plan needs; it does not need to be accurate, only monotonic.
    /// </summary>
    public static long WeighBundle(string dir)
    {
        try { return SafeDirectoryScan.Files(dir, "*.al").Count(); }
        catch { return 0; }
    }


    /// <summary>
    /// GC heaps to give one worker: always one.
    ///
    /// The runner ships under Server GC (#2577), which sizes its heap count from the CORE
    /// count — right for a single process, wrong under --jobs, where every worker
    /// independently believes it owns the whole machine and keeps an arena sized for it.
    ///
    /// This used to divide the core budget across workers (cores / jobs). That was a guess
    /// made before anything was measured, and measurement does not support it: one heap wins
    /// at every job count, not only at high ones. At --jobs 2 the old formula handed each
    /// worker 6 heaps and cost 2.5 GB of peak to save 6.8 s of wall. Figures are in
    /// ParallelFanOutGcHeapTests' header; the pass count is identical in every configuration.
    ///
    /// Both parameters are kept so the call site and the tests keep their shape, and so a
    /// future machine-dependent answer does not need a signature change — but the answer no
    /// longer depends on either, which is the point.
    /// </summary>
    public static int GcHeapCountForWorker(int cores, int jobs)
        => 1;

    /// <summary>
    /// The single-process default for the AL emit-phase timeout (Program.cs), in seconds. A
    /// worker under --jobs scales this by its shard count — see
    /// <see cref="EmitTimeoutSecForWorker"/> — so it stays the source of truth for both.
    /// </summary>
    public const int DefaultEmitTimeoutSec = 120;

    /// <summary>
    /// Emit-phase timeout to hand one worker, in seconds (issue #2715).
    ///
    /// The timeout is wall-clock, but under --jobs N every worker competes with N-1 others for
    /// the same cores — a bundle that emits comfortably alone can time out purely because other
    /// workers are running. Measured on a 12-core box at --jobs 12: Tests-ERM's emit was cut off
    /// at 120.1s of wall while that worker's total wall was 820.1s, roughly the same ~7x the
    /// contention stretched everything else by. Scaling the default by the shard count is a
    /// proxy for that stretch, not a precise CPU-time budget — see this file's header comment
    /// for why CPU time would be the more precise fix and why it is a larger change.
    /// </summary>
    public static int EmitTimeoutSecForWorker(int jobs)
        => DefaultEmitTimeoutSec * Math.Max(1, jobs);

    /// <summary>The single-process default for the per-test watchdog (TestExecutor), in
    /// seconds. Kept in step with TestExecutor.DefaultTestTimeoutSeconds by
    /// ParallelFanOutTestTimeoutTests, which fails if the two drift apart.</summary>
    public const int DefaultTestTimeoutSec = 60;

    /// <summary>
    /// Per-test watchdog timeout to hand one worker, in seconds (issue #2718).
    ///
    /// Same wall-clock-under-contention shape as the emit timeout above, but it needed
    /// measuring rather than assuming, because the two have very different margins and the
    /// cost of over-scaling is different in kind. The watchdog exists to stop a runaway AL
    /// loop; stretching it delays catching a real hang, which the emit timeout's equivalent
    /// does not.
    ///
    /// What the margin actually is, measured on a 12-core box (load ~4), single process,
    /// slowest SINGLE test:
    ///
    ///   al-language corpus (2,464 tests):   0.78s  ->  77x headroom to 60s
    ///   Tests-SMB, a BaseApp bucket (1,027): 4.36s  ->  13.8x headroom to 60s
    ///
    /// So the corpus cannot reach this watchdog through contention at any realistic job
    /// count, and BaseApp is the surface that can. Against the ~7x stretch #2715 measured at
    /// --jobs 12 (not the 12x a linear model predicts), 4.36s lands near 30s — about half the
    /// budget. That is a real margin, so this is NOT the demonstrated failure the emit
    /// timeout was; it is a plausible one with roughly 2x of room left.
    ///
    /// Two things close that gap and are why this scales anyway. The Tests-SMB figure is a
    /// LOWER bound: it was measured without --test-data, where most tests fail early and stop
    /// short of the work they exist to do (234 of 1,027 passed). And the cost of being wrong
    /// is lopsided — a spurious abort takes out the rest of its codeunit AND every later
    /// codeunit in the bundle (one such abort cost 6,097 tests across 189 codeunits), while
    /// over-scaling costs only bounded extra wall on a genuine hang, which still gets caught
    /// and still reports as a timeout.
    ///
    /// A CPU-time budget would be the precise fix and would also answer #2070's "the thread
    /// is legitimately blocked, not runaway" — contention inflates wall time and leaves CPU
    /// time alone, and a runaway loop burns CPU at full rate regardless of job count. That is
    /// the larger change this defers, exactly as the emit timeout deferred it.
    /// </summary>
    public static int TestTimeoutSecForWorker(int jobs)
        => DefaultTestTimeoutSec * Math.Max(1, jobs);

    /// <summary>
    /// Extra environment for a worker process: one GC heap, conserve memory, no background GC,
    /// and a shard-scaled emit timeout. The GC knobs together take about half off peak memory
    /// for 6-7% wall (8,183 -> 4,193 MB at --jobs 4, 4,983 -> 2,434 MB at --jobs 2, pass count
    /// identical in both) — see ParallelFanOutGcHeapTests' header for the measurements behind
    /// each one.
    ///
    /// Every knob here is SOFT: it can cost time, never correctness. That rules out
    /// DOTNET_GCHeapHardLimit, which recovers a similar amount and then silently drops
    /// Tests-SMB from 259 to 212 passing below about 1.25 GB, with no error and an unchanged
    /// exit code (#2712).
    ///
    /// Each knob is skipped when the user has already set it — someone tuning by hand, or a CI
    /// runner setting one globally, must win over a value chosen here. Setting one does not
    /// suppress the others.
    /// </summary>
    public static Dictionary<string, string> WorkerEnvironment(
        int cores,
        int jobs,
        string? userHeapCount = null,
        string? userConserveMemory = null,
        string? userGcConcurrent = null,
        string? userEmitTimeoutSec = null,
        string? userTestTimeoutSec = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(userHeapCount))
            env["DOTNET_GCHeapCount"] = GcHeapCountForWorker(cores, jobs).ToString();
        // 9 is the most aggressive setting: trade CPU for footprint wherever the GC can.
        if (string.IsNullOrEmpty(userConserveMemory))
            env["DOTNET_GCConserveMemory"] = "9";
        // 0 = background GC off. A background collection keeps its own budget alive, worth
        // ~150 MB of the measured difference on Tests-SMB.
        if (string.IsNullOrEmpty(userGcConcurrent))
            env["DOTNET_gcConcurrent"] = "0";
        if (string.IsNullOrEmpty(userEmitTimeoutSec))
            env["AL_RUNNER_EMIT_TIMEOUT_SEC"] = EmitTimeoutSecForWorker(jobs).ToString();
        // #2718. Note this is the ENV var only: an explicit --test-timeout reaches the child
        // as an argument and TestExecutor.TestTimeout() ranks that above the env var, so a
        // caller who named a number still gets exactly that number.
        if (string.IsNullOrEmpty(userTestTimeoutSec))
            env["AL_RUNNER_TEST_TIMEOUT_SEC"] = TestTimeoutSecForWorker(jobs).ToString();
        return env;
    }

    /// <summary>A bundle lighter than this many AL files (per piece) is never split across
    /// workers: each extra worker pays startup, bundle load and test-data company load again.
    /// 100 stays the default because it is right for bundles of fast tests; slow-per-test bundles
    /// gain from 20 (docs/jobs-unit-claiming.md § Floor). Override with <see cref="MinSplitFilesEnvVar"/>.</summary>
    public const long DefaultMinSplitFiles = 100;

    public const string MinSplitFilesEnvVar = "AL_RUNNER_JOBS_SPLIT_MIN_FILES";

    private static long MinSplitFiles()
        => long.TryParse(Environment.GetEnvironmentVariable(MinSplitFilesEnvVar), out var n) && n > 0
            ? n : DefaultMinSplitFiles;

    /// <summary>
    /// Why this run may not hand one bundle's test codeunits to several workers, or null when it
    /// may (#5130). Splitting needs the database rolled back between codeunits (SingleInstance
    /// state still is not, #4781; docs/jobs-unit-claiming.md), and nothing judging a bundle's own
    /// tests from inside one worker.
    /// </summary>
    public static string? SplitRefusal(TestIsolation isolation, bool countBaseline, bool expectationsRequireMatch)
    {
        if (isolation == TestIsolation.Disabled)
            return "--isolation disabled keeps state across every test, so a bundle stays in order on one worker";
        if (countBaseline)
            return "--count-baseline compares a bundle's whole test count inside one worker";
        if (expectationsRequireMatch)
            return "--expectations-require-match audits every entry against the tests one worker discovered";
        return null;
    }

    /// <summary>The shard plan: bundles balanced by weight, and (unless refused) heavy bundles cut
    /// across workers, as many as the free memory holds. See <see cref="ShardPlanner.PlanSplit"/>
    /// and <see cref="JobsMemory"/>.</summary>
    public static ShardPlanner.SplitPlan PlanBundles(IReadOnlyList<string> bundles, int jobs, string? splitRefusal)
        => PlanBundles(bundles, jobs, splitRefusal, JobsMemory.FreeBytes(), JobsMemory.Model);

    /// <summary>
    /// <see cref="PlanBundles(IReadOnlyList{string},int,string?)"/> over an explicit memory reading,
    /// so the sizing is testable. Every extra worker of a shared bundle pays the model's base again
    /// (the tests it runs are the same tests, shared out), so while the plan's estimate exceeds what
    /// is free, the split bundle with the most workers loses one (the smallest slowdown per
    /// worker given up), until it fits or nothing is shared. Only SHARING is limited: how many
    /// workers an unshared run uses stays the caller's <c>--jobs</c>, exactly as before. A null
    /// reading means unknown and sizes nothing.
    /// </summary>
    internal static ShardPlanner.SplitPlan PlanBundles(IReadOnlyList<string> bundles, int jobs,
        string? splitRefusal, long? freeBytes, JobsMemory.WorkerModel model)
    {
        var weighted = bundles.Select(b => (Name: b, Weight: WeighBundle(b))).ToList();
        if (splitRefusal != null)
            return new ShardPlanner.SplitPlan(ShardPlanner.Plan(weighted, jobs), new HashSet<string>());

        var plan = ShardPlanner.PlanSplit(weighted, jobs, MinSplitFiles());
        if (freeBytes == null || plan.SplitBundles.Count == 0) return plan;

        var weight = weighted.ToDictionary(w => w.Name, w => w.Weight, StringComparer.Ordinal);
        var counted = bundles.ToDictionary(b => b, CountTests, StringComparer.Ordinal);
        // A bundle whose tests cannot be counted is unknown, not empty: sizing from it would
        // call the plan cheap exactly when it could not be measured.
        if (counted.Values.Any(c => c == null)) return plan;
        var tests = counted.ToDictionary(kv => kv.Key, kv => kv.Value!.Value, StringComparer.Ordinal);
        var budget = (long)(freeBytes.Value * JobsMemory.Headroom);
        var wanted = plan.SplitBundles.ToDictionary(b => b, b => SharingWorkers(plan, b), StringComparer.Ordinal);
        var cap = new Dictionary<string, int>(StringComparer.Ordinal);
        while (plan.SplitBundles.Count > 0 && EstimateBytes(plan, weight, tests, model) > budget)
        {
            var victim = plan.SplitBundles
                .OrderByDescending(b => SharingWorkers(plan, b)).ThenByDescending(b => weight[b])
                .ThenBy(b => b, StringComparer.Ordinal).First();
            cap[victim] = SharingWorkers(plan, victim) - 1;
            plan = ShardPlanner.PlanSplit(weighted, jobs, MinSplitFiles(), cap);
        }
        if (cap.Count == 0) return plan;

        var changed = wanted.Where(kv => SharingWorkers(plan, kv.Key) < kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Reporter.BundleLabel(kv.Key)} by {SharingWorkers(plan, kv.Key)} worker(s), not {kv.Value}");
        return plan with
        {
            MemoryNote = "free memory holds fewer workers per bundle than --jobs asked for: "
                + string.Join("; ", changed)
                + $" ({FormatGb(freeBytes.Value)} GB free; {JobsMemory.FreeMemoryEnvVar} overrides the reading)",
        };
    }

    private static string FormatGb(long bytes)
        => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static int SharingWorkers(ShardPlanner.SplitPlan plan, string bundle)
        => plan.Shards.Count(s => s.Any(x => x.Name == bundle));

    /// <summary>What the plan's workers are expected to need together. Each worker costs the
    /// model's base however little it runs, plus the tests it will run: a piece of a shared bundle
    /// runs its share of the bundle's tests, by the piece's share of the bundle's files.</summary>
    internal static long EstimateBytes(ShardPlanner.SplitPlan plan, IReadOnlyDictionary<string, long> fullWeight,
        IReadOnlyDictionary<string, long> tests, JobsMemory.WorkerModel model)
        => plan.Shards.Sum(shard => model.EstimateBytes(shard.Sum(piece =>
            fullWeight[piece.Name] == 0 ? 0.0 : (double)tests[piece.Name] * piece.Weight / fullWeight[piece.Name])));

    private static readonly System.Text.RegularExpressions.Regex TestAttribute =
        new(@"^\s*\[Test\]", System.Text.RegularExpressions.RegexOptions.Multiline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>How many <c>[Test]</c> methods a bundle declares, read from its AL text: the number
    /// its workers will run between them. Null when it cannot be read.</summary>
    public static long? CountTests(string dir)
    {
        long n = 0;
        try
        {
            foreach (var f in SafeDirectoryScan.Files(dir, "*.al"))
                n += TestAttribute.Matches(File.ReadAllText(f)).Count;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return n;
    }

    /// <summary>
    /// Run <paramref name="bundles"/> across <paramref name="jobs"/> worker processes and print
    /// one aggregate summary. Returns the exit code: the worst any worker returned, so a green
    /// aggregate cannot hide a shard that failed. <paramref name="jsonStdout"/> is the real stdout
    /// when Program.cs redirected it for --output-json: the one document goes there.
    /// </summary>
    public static int Run(IReadOnlyList<string> bundles, IReadOnlyList<string> originalArgs, int jobs,
        string? splitRefusal, TextWriter? jsonStdout = null)
    {
        var plan = PlanBundles(bundles, jobs, splitRefusal);
        var shards = plan.Shards;

        // ScratchDirs (#2706): the delete at the end of this method only runs on a clean exit,
        // and a --jobs run is exactly the kind that gets killed; the sidecar lets the next
        // runner start reclaim it.
        var tempDir = ScratchDirs.Create(Path.Combine(Path.GetTempPath(), "al-runner-jobs-" + Guid.NewGuid().ToString("N")));

        var exe = Environment.ProcessPath ?? "dotnet";
        var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var viaDotnet = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                     || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        // NOT a "[jobs]"-style tag: Log's FilteredWriter drops [Component]-prefixed lines at
        // default verbosity, and this is the run's own plan — which bundles went to which
        // worker — not debug chatter. Tagging it hid it completely, the same way the
        // "[bc] selected BC" line was once hidden (and cost 42 tests before anyone noticed).
        Console.WriteLine($"jobs: {bundles.Count} bundle(s) across {shards.Count} worker process(es)");
        // #5267: a bundle's source dependencies that another shard runs. Worked out once per shard
        // here, and handed to that worker as extra, dependency-only folders (below).
        var dependencyOnly = shards.Select(s => JobsSourceDependencies.OutsideShard(
            s.Select(x => x.Name).ToList(), bundles)).ToList();
        for (var i = 0; i < shards.Count; i++)
            Console.WriteLine($"jobs:   shard {i}: {shards[i].Count} bundle(s), weight {shards[i].Sum(x => x.Weight)}"
                + (dependencyOnly[i].Count == 0 ? "" : $", plus {dependencyOnly[i].Count} source dependency folder(s) "
                    + "it compiles and does not run: "
                    + string.Join(", ", dependencyOnly[i].Select(Reporter.BundleLabel))));
        if (plan.MemoryNote != null) Console.WriteLine($"jobs: {plan.MemoryNote}");
        foreach (var name in plan.SplitBundles.OrderBy(n => n, StringComparer.Ordinal))
            Console.WriteLine($"jobs:   {name} is shared by {shards.Count(sh => sh.Any(x => x.Name == name))} "
                + "worker(s): they claim its test codeunits first come, first served");
        if (splitRefusal != null && PlanBundles(bundles, jobs, null).SplitBundles.Count > 0)
            Console.WriteLine($"jobs: not splitting a bundle across workers: {splitRefusal}");

        // The claim directory lives under tempDir so ScratchDirs reclaims it with everything else.
        string? claimDir = null;
        if (plan.SplitBundles.Count > 0)
        {
            claimDir = Path.Combine(tempDir, "claims");
            Directory.CreateDirectory(claimDir);
        }

        // #5129: --out and --output-json are written here, from what each worker hands back.
        var callerOut = LastValueOf(originalArgs, "--out");
        var callerJson = originalArgs.Contains("--output-json");
        var shardResultFiles = Enumerable.Range(0, shards.Count)
            .Select(i => Path.Combine(tempDir, $"shard-{i}.results.json")).ToList();

        var procs = new List<(System.Diagnostics.Process P, string Junit, Task<string> Out, Task<string> Err)>();
        for (var i = 0; i < shards.Count; i++)
        {
            var junit = Path.Combine(tempDir, $"shard-{i}.xml");
            var childArgs = BuildChildArgs(originalArgs,
                shards[i].Select(x => x.Name).Concat(dependencyOnly[i]).ToList(), bundles, junit);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // #3738: a worker writes UTF-8, so decode UTF-8 — do not inherit whatever this
                // parent's console happens to be. Leaving these null works only while the
                // parent's own OutputEncoding assignment succeeded; if it took the documented
                // degradation path while a worker's succeeded, the parent would decode a
                // worker's UTF-8 as its old code page and reprint mojibake (PR #3795 review).
                StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            foreach (var kv in WorkerEnvironment(
                         Environment.ProcessorCount, shards.Count,
                         Environment.GetEnvironmentVariable("DOTNET_GCHeapCount"),
                         Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory"),
                         Environment.GetEnvironmentVariable("DOTNET_gcConcurrent"),
                         Environment.GetEnvironmentVariable("AL_RUNNER_EMIT_TIMEOUT_SEC"),
                         Environment.GetEnvironmentVariable("AL_RUNNER_TEST_TIMEOUT_SEC")))
                psi.Environment[kv.Key] = kv.Value;
            psi.Environment[TestSelectionAudit.WorkerEnvVar] = "1";
            if (callerOut != null || callerJson) psi.Environment[JobsReports.ShardResultsEnvVar] = shardResultFiles[i];
            JobsSourceDependencies.ApplyTo(psi.Environment, dependencyOnly[i]);
            if (claimDir != null)
            {
                psi.Environment[UnitClaimQueue.DirEnvVar] = claimDir;
                psi.Environment[UnitClaimQueue.BundlesEnvVar] =
                    string.Join("|", plan.SplitBundles.Select(b => Normalize(b)));
            }
            if (viaDotnet && asm != null) psi.ArgumentList.Add(asm);
            foreach (var a in childArgs) psi.ArgumentList.Add(a);

            var p = System.Diagnostics.Process.Start(psi)!;
            // Read both pipes on their own tasks BEFORE waiting. A child that fills a pipe
            // buffer blocks forever otherwise, and the parent waits on a process that can never
            // finish — the same shape as the WaitForExit(int)-without-draining hang this repo
            // has already been bitten by.
            procs.Add((p, junit, p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync()));
        }

        var worst = 0;
        long tests = 0, failures = 0, errors = 0, skipped = 0;
        long selected = 0;
        var selectionUnreported = false;
        long notRun = 0, partial = 0;
        var shardOutput = new string[procs.Count];
        var shardResumed = new bool[procs.Count];
        var shardHandedBack = new bool[procs.Count];
        var shardEnded = new string[procs.Count];
        for (var i = 0; i < procs.Count; i++)
        {
            var (p, junit, so, se) = procs[i];
            var killed = WaitForWorkerExit(p, junit, i);
            shardEnded[i] = killed ? "killed" : $"exit {p.ExitCode}";
            var stdout = so.GetAwaiter().GetResult();
            var stderr = se.GetAwaiter().GetResult();

            Console.WriteLine();
            Console.WriteLine($"───── shard {i} (exit {(killed ? "killed" : p.ExitCode.ToString())}) ─────");
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());

            // A worker writes its JUnit file when it reaches its output block, so no readable file means it
            // stopped before reporting anything (a provisioning gap, a crash): #5333.
            var handedBack = JUnitCounts.TryRead(junit, out var c);
            shardHandedBack[i] = handedBack;
            tests += c.Tests; failures += c.Failures; errors += c.Errors; skipped += c.Skipped;

            var shardSelected = TestSelectionAudit.ReadWorkerLines(stderr);
            if (shardSelected == null) selectionUnreported = true;
            else selected += shardSelected.Value;

            var exit = killed ? ExitCodeForKilledWorker(c) : p.ExitCode;
            if (exit > worst) worst = exit;

            // A bundle that COMPILE FAILs (Reporter.cs's "=== <bundle> — COMPILE FAIL ===")
            // contributes zero tests to the JUnit this worker writes, so it vanishes from the
            // totals above with no trace — the exact shape #2715 measured (40,550 tests down to
            // 14,856, reported as a plain total). Count it here so the aggregate says a bundle
            // is missing instead of silently reporting the smaller number as complete.
            //
            // BOTH headers, since #2779: a bundle that compiled and then failed at RUN time now
            // reports "— EXEC FAIL ===" instead. It contributes zero tests for exactly the same
            // reason, so counting only the compile header would have re-introduced #2715's
            // silent loss for every execution failure.
            //
            // A worker that left no JUnit file printed no header: what it lost is counted from the
            // bundles it was handed (CountWorkersThatLeftNothing, #5333), and nothing it printed is read,
            // so one lost bundle is never taken from both routes.
            if (!handedBack)
            {
                shardOutput[i] = "";
                Console.Error.WriteLine($"jobs: shard {i} ended ({shardEnded[i]}) without writing its test results; "
                    + "its bundle(s) are MISSING from the totals: " + string.Join(", ", shards[i].Select(x => x.Name)));
                continue;
            }
            shardOutput[i] = stdout + "\n" + stderr;
            shardResumed[i] = AbortResume.WasResumed(stderr);
            var lost = CountLostBundles(stdout, stderr);
            notRun += lost.NotRun;

            // #2762: the sibling shape. A bundle that lost SOME suites and still produced tests
            // is not "not run" — its survivors ARE in the totals above, so counting it as
            // missing would overstate the loss. But it covers less than it declares, and the
            // aggregate is the only summary a --jobs caller reads; without this the parent
            // reprints a clean total for a run in which whole suites never compiled.
            partial += lost.Partial;
        }

        var leftNothing = CountWorkersThatLeftNothing(
            shards.Select(sh => (IReadOnlyList<string>)sh.Select(x => x.Name).ToList()).ToList(),
            shardHandedBack, shardOutput, shardResumed);
        notRun += leftNothing.NotRun;
        partial += leftNothing.Partial;
        var lostWorkers = Enumerable.Range(0, shards.Count).Where(sh => !shardHandedBack[sh]).ToList();

        // A bundle several workers share prints its COMPILE FAIL / EXEC FAIL / SUITE ERRORS header
        // once per worker. It is one missing bundle, so take the extra sightings back out. Only the
        // workers that reported are compared: a lost one printed nothing, and is counted above.
        foreach (var name in plan.SplitBundles)
        {
            var sharing = Enumerable.Range(0, shards.Count)
                .Where(sh => shardHandedBack[sh] && shards[sh].Any(x => x.Name == name)).ToList();
            var label = Reporter.BundleLabel(name);
            foreach (var header in NotRunHeaders)
                notRun -= ExtraSightings(shardOutput, sharing, $"=== {label}{header}", shardResumed);
            partial -= ExtraSightings(shardOutput, sharing, $"=== {label}{PartialLossHeader}", shardResumed);
        }

        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine($"al-runner — aggregate across {shards.Count} worker process(es)");
        Console.WriteLine("=================================================================");
        // The same counts line Reporter.PrintSummary prints (#4562), so one reader serves both.
        Console.WriteLine($"Tests: {tests}   passed {tests - failures - errors - skipped}   "
            + $"failed {failures}   errors {errors}   skipped {skipped}");
        var stopped = lostWorkers.Count == 0 ? ""
            : $", or held by shard {string.Join(", ", lostWorkers)}, which stopped before it reported";
        if (notRun > 0)
            Console.WriteLine($"  NOT RUN:     {notRun} bundle(s) — COMPILE FAIL or EXEC FAIL in a " +
                               $"shard above{stopped}, excluded from the totals; see that shard's output for which one");
        if (partial > 0)
            Console.WriteLine($"  PARTIAL:     {partial} bundle(s) — SUITE ERRORS in a shard above{stopped}: they " +
                               "ran, but the tests the lost suites declare are MISSING from the totals");
        Console.WriteLine("=================================================================");

        // #4055: the workers only reported what --test selected; the verdict is the run's.
        // A shard that did not report, or any exit above "tests ran", leaves the zero unattributable.
        var testFilter = LastValueOf(originalArgs, "--test", "--filter");
        if (testFilter != null && selected == 0 && tests == 0 && !selectionUnreported
            && notRun == 0 && partial == 0 && (worst == 0 || worst == 5))
        {
            Console.Error.WriteLine("test-selection: " + TestSelectionAudit.Describe(testFilter));
            worst = TestSelectionAudit.ExitCode;
        }

        // #5129: BuildChildArgs hands every worker its own files, so the paths the caller named are
        // written here from those files or not at all.
        var lostOutputs = new List<string>();
        var lostShardCount = 0;
        List<BucketResult>? runBuckets = null;
        if (callerOut != null || callerJson)
        {
            var (buckets, lostShards) = JobsReports.Merge(shardResultFiles,
                shards.Select(s => (IReadOnlyList<string>)s.Select(x => x.Name).ToList()).ToList(), bundles, shardEnded);
            runBuckets = buckets;
            lostShardCount = lostShards.Count;
            foreach (var i in lostShards)
                Console.Error.WriteLine($"jobs: shard {i} ended ({shardEnded[i]}) without handing back its "
                    + "results; its bundles are listed as failed to execute in the report");
        }
        if (callerOut != null)
        {
            var problem = OutputPaths.TryWrite("--out", callerOut, () => Reporter.WriteClassification(runBuckets!, callerOut));
            if (problem != null) { Console.Error.WriteLine(problem); lostOutputs.Add("--out"); }
            else Console.WriteLine($"Classification -> {callerOut} (merged from {procs.Count} worker(s))");
        }
        var callerJunit = LastValueOf(originalArgs, "--output-junit");
        if (callerJunit != null)
        {
            var problem = OutputPaths.TryWrite("--output-junit", callerJunit,
                () => JUnitReport.WriteMergedJUnit(callerJunit, procs.Select(x => x.Junit).ToList()));
            if (problem != null) { Console.Error.WriteLine(problem); lostOutputs.Add("--output-junit"); }
            else Console.WriteLine($"JUnit XML -> {callerJunit} (merged from {procs.Count} worker(s))");
        }
        if (lostOutputs.Count > 0)
            Console.Error.WriteLine($"jobs: could not write {string.Join(", ", lostOutputs)} (see the message above)");
        worst = JobsReports.Escalate(worst, lostShardCount, lostOutputs.Count);
        if (callerJson)
        {
            // After every escalation above: the document's `exitCode` is the run's, as in a plain run.
            // Program.cs redirected Console.Out to stderr for --output-json and handed us the real one.
            (jsonStdout ?? Console.Out).WriteLine(Reporter.SerializeJsonOutput(runBuckets!, worst));
        }

        // The run's one `Result:` line, after every escalation above; the shards label theirs.
        var strictExit = StrictExit(originalArgs);
        Console.WriteLine();
        Console.WriteLine(Reporter.ResultLine(strictExit ? worst : 0, strictExit ? null : worst));
        ScratchDirs.Release(tempDir);
        return strictExit ? worst : 0;
    }

    /// <summary>False when the caller's last word on exit codes is `--no-strict-exit`: Program.cs
    /// lets `--strict` and `--no-strict-exit` override each other in argument order.</summary>
    internal static bool StrictExit(IReadOnlyList<string> args)
    {
        var strict = true;
        foreach (var a in args)
        {
            if (a == "--strict") strict = true;
            else if (a == "--no-strict-exit") strict = false;
        }
        return strict;
    }

    private static string? LastValueOf(IReadOnlyList<string> args, params string[] flags)
    {
        string? value = null;
        for (var i = 0; i + 1 < args.Count; i++)
            if (flags.Contains(args[i])) value = args[++i];
        return value;
    }

    /// <summary>
    /// How long a worker may stay alive AFTER it has written its JUnit file before the parent
    /// kills it (#2704). The file is written after the summary, so once it exists the worker's
    /// work is done and all that remains is process exit — well under a second when healthy.
    /// Before the file exists the wait is unbounded: a long shard is --test-timeout's business.
    /// Not a CLI flag on purpose; nothing legitimate takes this long to exit.
    /// </summary>
    public static readonly TimeSpan WorkerExitGrace = TimeSpan.FromSeconds(60);

    /// <summary>Pure kill policy — see <see cref="WorkerExitGrace"/>.</summary>
    public static bool ShouldKillWorker(bool junitWritten, TimeSpan sinceJunitWritten, TimeSpan grace)
        => junitWritten && sinceJunitWritten >= grace;

    /// <summary>
    /// A killed worker's Process.ExitCode is the kill signal, not its verdict. It had already
    /// written its results, so derive the verdict the way Program.cs does: any failure or
    /// error → 1, otherwise 0.
    /// </summary>
    public static int ExitCodeForKilledWorker(JUnitTotals counts)
        => counts.Failures + counts.Errors > 0 ? 1 : 0;

    /// <summary>
    /// Wait for a worker, bounded once its JUnit file has appeared. Returns true when the
    /// worker had to be killed. #2704: a worker whose BC-internal foreground thread outlived
    /// Main printed its summary and never exited; a bare WaitForExit() here then hung the
    /// whole --jobs run with no diagnostic and no partial results, even though every other
    /// worker's output was already in hand. Stdout EOF cannot be the "done" signal — a hung
    /// child never closes its pipes — the JUnit file is.
    /// </summary>
    private static bool WaitForWorkerExit(System.Diagnostics.Process p, string junitPath, int shard)
    {
        var junitSeen = System.Diagnostics.Stopwatch.StartNew();
        var junitWritten = false;
        while (!p.WaitForExit(500))
        {
            if (!junitWritten && File.Exists(junitPath))
            {
                junitWritten = true;
                junitSeen.Restart();
            }
            if (!ShouldKillWorker(junitWritten, junitSeen.Elapsed, WorkerExitGrace)) continue;

            // Not "[jobs]"-tagged: FilteredWriter would drop it, and this is the one line that
            // tells the reader why the exit column says "killed".
            Console.Error.WriteLine(
                $"jobs: shard {shard} (pid {p.Id}) wrote its results but did not exit within " +
                System.FormattableString.Invariant($"{WorkerExitGrace.TotalSeconds:F0}s")
                + " — killing its process tree and reporting the " +
                "results it wrote (a foreground thread outliving Main; see issue #2704)");
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(10_000);
            return true;
        }
        return false;
    }

    internal static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return p.TrimEnd('/', '\\'); }
    }

    /// <summary>The per-bundle headers Reporter.PrintPerTest writes for a bundle that produced
    /// no tests. Both mean "this bundle is absent from the JUnit totals"; which one appears
    /// depends only on WHAT failed (#2779).</summary>
    internal static readonly IReadOnlyList<string> NotRunHeaders =
        new[] { " — COMPILE FAIL ===", " — EXEC FAIL ===" };

    /// <summary>The per-bundle header Reporter.PrintPerTest writes for a bundle that RAN but
    /// lost one or more suites (#2762). Deliberately NOT in <see cref="NotRunHeaders"/>: such a
    /// bundle's surviving tests are counted in the JUnit totals, so it is under-reported, not
    /// absent. Matched without the count so any number of lost suites is found.</summary>
    internal const string PartialLossHeader = " — SUITE ERRORS (";

    /// <summary>The sightings of <paramref name="needle"/> beyond the first, when EVERY worker in
    /// <paramref name="sharing"/> printed it: that is one bundle failing identically on each of
    /// them. A worker that did not print it did not fail on it, and nothing is taken back.</summary>
    internal static int ExtraSightings(IReadOnlyList<string> shardOutput, IReadOnlyList<int> sharing, string needle,
        IReadOnlyList<bool>? shardResumed = null)
    {
        if (sharing.Count < 2) return 0;
        var perShard = sharing.Select(sh => CountAcrossAttempts(shardOutput[sh], needle, shardResumed?[sh] ?? false)).ToList();
        var common = perShard.Min();
        return common * (sharing.Count - 1);
    }

    // The summary line each attempt of a worker prints (Reporter.PrintSummary, #4562), whole: a failing
    // test's message is printed at column 0 too, and a prefix match would take a quoted one for an attempt.
    private static readonly System.Text.RegularExpressions.Regex AttemptSummaryLine =
        new(@"^Tests: \d+   passed \d+   failed \d+.*        Time: [\d.]+ s \(wall [\d.]+ s\)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>A worker that resumed after a watchdog abort (#2280) wrote every attempt into the one output,
    /// each ending in its own summary line: cut there, so a bundle failing the same way in the next attempt
    /// is not a second bundle. Only a worker that RESUMED is cut (<paramref name="resumed"/>, from its
    /// stderr): anything else is one attempt however its output reads, because a cut inside one attempt
    /// can fold two bundles of one label into one and hide a lost bundle (#2715).</summary>
    internal static IReadOnlyList<string> Attempts(string output, bool resumed)
    {
        if (!resumed) return new[] { output };
        var attempts = new List<string>();
        var from = 0;
        foreach (System.Text.RegularExpressions.Match m in AttemptSummaryLine.Matches(output))
        {
            attempts.Add(output.Substring(from, m.Index + m.Length - from));
            from = m.Index + m.Length;
        }
        if (from < output.Length) attempts.Add(output.Substring(from));
        return attempts;
    }

    /// <summary><paramref name="needle"/> counted in the attempt that printed it most, never summed over
    /// attempts: a resumed worker reruns its bundles, so the same bundle reports it once per attempt.</summary>
    internal static int CountAcrossAttempts(string output, string needle, bool resumed)
        => Attempts(output, resumed).Select(a => CountOccurrences(a, needle)).DefaultIfEmpty(0).Max();

    /// <summary>The bundles the workers that wrote no JUnit file cost the aggregate (#5333). Such a worker
    /// reached no output block, so nothing it was handed is in the totals and it printed no
    /// <c>COMPILE FAIL</c> / <c>EXEC FAIL</c> header for the NOT RUN count to read: every bundle it holds is
    /// NOT RUN, counted once however many workers hold it. A bundle that another worker held, ran and
    /// reported is missing only the lost worker's part of it, so it is PARTIAL; and a bundle such a worker
    /// already printed a header for was counted from that header, so it is not counted again.
    /// <paramref name="shardOutput"/> is read for the workers that handed back (a lost worker's is not).</summary>
    internal static (int NotRun, int Partial) CountWorkersThatLeftNothing(
        IReadOnlyList<IReadOnlyList<string>> shardBundles, IReadOnlyList<bool> handedBack,
        IReadOnlyList<string> shardOutput, IReadOnlyList<bool> shardResumed)
    {
        int notRun = 0, partial = 0;
        var counted = new HashSet<string>(StringComparer.Ordinal);
        for (var lost = 0; lost < shardBundles.Count; lost++)
        {
            if (handedBack[lost]) continue;
            foreach (var bundle in shardBundles[lost])
            {
                if (!counted.Add(bundle)) continue;
                var ran = Enumerable.Range(0, shardBundles.Count)
                    .Where(sh => handedBack[sh] && shardBundles[sh].Contains(bundle)).ToList();
                if (ran.Count == 0) { notRun++; continue; }
                var label = Reporter.BundleLabel(bundle);
                var reported = ran.Any(sh => NotRunHeaders.Append(PartialLossHeader)
                    .Any(h => CountAcrossAttempts(shardOutput[sh], $"=== {label}{h}", shardResumed[sh]) > 0));
                if (!reported) partial++;
            }
        }
        return (notRun, partial);
    }

    /// <summary>The bundles one worker reports as not run (COMPILE FAIL / EXEC FAIL) and as partial (SUITE
    /// ERRORS), from its captured output. Headers are on stdout; stderr is counted as one attempt.</summary>
    internal static (int NotRun, int Partial) CountLostBundles(string stdout, string stderr)
    {
        var resumed = AbortResume.WasResumed(stderr);
        var notRun = NotRunHeaders.Sum(h => CountBundleHeaders(stdout, h, resumed) + CountBundleHeaders(stderr, h, false));
        var partial = CountBundleHeaders(stdout, PartialLossHeader, resumed) + CountBundleHeaders(stderr, PartialLossHeader, false);
        return (notRun, partial);
    }

    /// <summary>How many bundles a worker's output reports under <paramref name="headerSuffix"/> (` — SUITE
    /// ERRORS (`, ` — COMPILE FAIL ===`): per bundle label, the most any one attempt printed, summed over
    /// labels. A bundle that fails identically in every attempt of a resumed worker is one bundle (#5269).</summary>
    internal static int CountBundleHeaders(string output, string headerSuffix, bool resumed)
    {
        var mostInOneAttempt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var attempt in Attempts(output, resumed))
        {
            var inThisAttempt = new Dictionary<string, int>(StringComparer.Ordinal);
            var idx = 0;
            while ((idx = attempt.IndexOf(headerSuffix, idx, StringComparison.Ordinal)) >= 0)
            {
                // The label is what the line carries between its `=== ` and the suffix; a header with
                // no `=== ` before it (never printed today) counts under the whole prefix.
                var lineStart = attempt.LastIndexOf('\n', idx) + 1;
                var prefix = attempt.Substring(lineStart, idx - lineStart);
                var open = prefix.LastIndexOf("=== ", StringComparison.Ordinal);
                var label = open >= 0 ? prefix.Substring(open + 4) : prefix;
                inThisAttempt[label] = inThisAttempt.GetValueOrDefault(label) + 1;
                idx += headerSuffix.Length;
            }
            foreach (var (label, n) in inThisAttempt)
                if (n > mostInOneAttempt.GetValueOrDefault(label)) mostInOneAttempt[label] = n;
        }
        return mostInOneAttempt.Values.Sum();
    }

    /// <summary>How many times a bundle reported COMPILE FAIL or EXEC FAIL in a shard's captured
    /// output — used to tell the aggregate summary how many bundles are missing from its totals,
    /// rather than reporting the smaller total as though it were complete (#2715).</summary>
    internal static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
