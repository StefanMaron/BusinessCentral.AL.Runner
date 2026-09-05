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
        "--exclude-test", "--resume-aborts", "--merge-counts",
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
        string? userEmitTimeoutSec = null)
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

        var tempDir = Path.Combine(Path.GetTempPath(), "al-runner-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

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
                         Environment.GetEnvironmentVariable("AL_RUNNER_EMIT_TIMEOUT_SEC")))
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
        long tests = 0, failures = 0, errors = 0, skipped = 0, notRun = 0;
        for (var i = 0; i < procs.Count; i++)
        {
            var (p, junit, so, se) = procs[i];
            p.WaitForExit();
            var stdout = so.GetAwaiter().GetResult();
            var stderr = se.GetAwaiter().GetResult();
            if (p.ExitCode > worst) worst = p.ExitCode;

            Console.WriteLine();
            Console.WriteLine($"───── shard {i} (exit {p.ExitCode}) ─────");
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());

            var c = JUnitCounts.Read(junit);
            tests += c.Tests; failures += c.Failures; errors += c.Errors; skipped += c.Skipped;

            // A bundle that COMPILE FAILs (Reporter.cs's "=== <bundle> — COMPILE FAIL ===")
            // contributes zero tests to the JUnit this worker writes, so it vanishes from the
            // totals above with no trace — the exact shape #2715 measured (40,550 tests down to
            // 14,856, reported as a plain total). Count it here so the aggregate says a bundle
            // is missing instead of silently reporting the smaller number as complete.
            notRun += CountOccurrences(stdout, " — COMPILE FAIL ===")
                    + CountOccurrences(stderr, " — COMPILE FAIL ===");
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
            Console.WriteLine($"  NOT RUN:     {notRun} bundle(s) — COMPILE FAIL in a shard above, " +
                               "excluded from the totals; see that shard's output for which one");
        Console.WriteLine("=================================================================");

        try { Directory.Delete(tempDir, recursive: true); } catch { }
        return worst;
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return p.TrimEnd('/', '\\'); }
    }

    /// <summary>How many times a bundle reported COMPILE FAIL in a shard's captured output —
    /// used to tell the aggregate summary how many bundles are missing from its totals, rather
    /// than reporting the smaller total as though it were complete (#2715).</summary>
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
