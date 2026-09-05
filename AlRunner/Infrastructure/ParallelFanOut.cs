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
//   * only this shard's bundles are passed, or every worker runs everything and the aggregate
//     double-counts.
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
        "--artifact-path", "--bc-version", "--cache", "--count-baseline", "--country",
        "--coverage-out", "--define", "--dump-csharp", "--expectations", "--filter",
        "--isolation", "--out", "--output-junit", "--package-cache", "--preprocessor-symbols",
        "--resolve-version", "--test", "--test-data-company", "--test-isolation",
        "--test-timeout", "--jobs",
        // Both take a value and both must reach a worker: a shard that lost --exclude-test would
        // walk straight back into the hang the parent already excluded, and one that lost
        // --resume-aborts would fall back to the default budget and start its own resume chain.
        "--exclude-test", "--resume-aborts", "--merge-counts", "--merge-results",
    };

    /// <summary>
    /// The command line for one worker: the parent's own arguments, with the bundle positionals
    /// replaced by <paramref name="shardBundles"/>, `--jobs` removed, and `--output-junit`
    /// forced to <paramref name="junitPath"/> so the parent can aggregate.
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

    /// <summary>
    /// Run <paramref name="bundles"/> across <paramref name="jobs"/> worker processes and print
    /// one aggregate summary. Returns the exit code: the worst any worker returned, so a green
    /// aggregate cannot hide a shard that failed.
    /// </summary>
    public static int Run(IReadOnlyList<string> bundles, IReadOnlyList<string> originalArgs, int jobs)
    {
        var weighted = bundles.Select(b => (Name: b, Weight: WeighBundle(b))).ToList();
        var shards = ShardPlanner.Plan(weighted, jobs);

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
        for (var i = 0; i < shards.Count; i++)
            Console.WriteLine($"jobs:   shard {i}: {shards[i].Count} bundle(s), weight {shards[i].Sum(x => x.Weight)}");

        var procs = new List<(System.Diagnostics.Process P, string Junit, Task<string> Out, Task<string> Err)>();
        for (var i = 0; i < shards.Count; i++)
        {
            var junit = Path.Combine(tempDir, $"shard-{i}.xml");
            var childArgs = BuildChildArgs(originalArgs, shards[i].Select(x => x.Name).ToList(), bundles, junit);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var kv in WorkerEnvironment(
                         Environment.ProcessorCount, shards.Count,
                         Environment.GetEnvironmentVariable("DOTNET_GCHeapCount"),
                         Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory"),
                         Environment.GetEnvironmentVariable("DOTNET_gcConcurrent"),
                         Environment.GetEnvironmentVariable("AL_RUNNER_EMIT_TIMEOUT_SEC"),
                         Environment.GetEnvironmentVariable("AL_RUNNER_TEST_TIMEOUT_SEC")))
                psi.Environment[kv.Key] = kv.Value;
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
        long tests = 0, failures = 0, errors = 0, skipped = 0, notRun = 0, partial = 0;
        for (var i = 0; i < procs.Count; i++)
        {
            var (p, junit, so, se) = procs[i];
            var killed = WaitForWorkerExit(p, junit, i);
            var stdout = so.GetAwaiter().GetResult();
            var stderr = se.GetAwaiter().GetResult();

            Console.WriteLine();
            Console.WriteLine($"───── shard {i} (exit {(killed ? "killed" : p.ExitCode.ToString())}) ─────");
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());

            var c = JUnitCounts.Read(junit);
            tests += c.Tests; failures += c.Failures; errors += c.Errors; skipped += c.Skipped;

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
            foreach (var header in NotRunHeaders)
                notRun += CountOccurrences(stdout, header) + CountOccurrences(stderr, header);

            // #2762: the sibling shape. A bundle that lost SOME suites and still produced tests
            // is not "not run" — its survivors ARE in the totals above, so counting it as
            // missing would overstate the loss. But it covers less than it declares, and the
            // aggregate is the only summary a --jobs caller reads; without this the parent
            // reprints a clean total for a run in which whole suites never compiled.
            partial += CountOccurrences(stdout, PartialLossHeader)
                     + CountOccurrences(stderr, PartialLossHeader);
        }

        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine($"al-runner — aggregate across {shards.Count} worker process(es)");
        Console.WriteLine("=================================================================");
        Console.WriteLine($"Tests:         {tests} total");
        Console.WriteLine($"  pass:        {tests - failures - errors - skipped}");
        Console.WriteLine($"  fail:        {failures}");
        Console.WriteLine($"  error:       {errors}");
        Console.WriteLine($"  skipped:     {skipped}");
        if (notRun > 0)
            Console.WriteLine($"  NOT RUN:     {notRun} bundle(s) — COMPILE FAIL or EXEC FAIL in a " +
                               "shard above, excluded from the totals; see that shard's output for which one");
        if (partial > 0)
            Console.WriteLine($"  PARTIAL:     {partial} bundle(s) — SUITE ERRORS in a shard above: they " +
                               "ran, but the tests the lost suites declare are MISSING from the totals");
        Console.WriteLine("=================================================================");

        ScratchDirs.Release(tempDir);
        return worst;
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
                $"{WorkerExitGrace.TotalSeconds:F0}s — killing its process tree and reporting the " +
                "results it wrote (a foreground thread outliving Main; see issue #2704)");
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(10_000);
            return true;
        }
        return false;
    }

    private static string Normalize(string p)
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
