// Reporter — aggregates per-test results into per-bucket and overall summaries,
// and writes a JSON failure-classification file for follow-up parallel work.
using System.Text.Json;
using static System.FormattableString;

namespace AlRunner;

public enum BucketStage { CompileFailed, ExecuteFailed, Ran }

public sealed record BucketResult(string BucketPath, BucketStage Stage,
                                   IReadOnlyList<string> CompileErrors,
                                   string? ProcessError,
                                   IReadOnlyList<TestResult> Tests,
                                   TimeSpan EmitTime, TimeSpan CompileTime, TimeSpan RunTime,
                                   // Number of app groups (bundled mode) / suites (--per-suite) that
                                   // actually executed to completion — i.e. reached the point of
                                   // contributing their tests to `Tests`, not merely discovered on
                                   // disk. Optional/trailing so existing call sites (tests included)
                                   // that don't care about it keep compiling unchanged. Consumed by
                                   // Infrastructure.CountBaselineCheck (#1880) as a second, coarser
                                   // signal alongside the test-count baseline — see that file's header
                                   // for why a whole-group-vanished bug can hide behind an unchanged
                                   // test count.
                                   int RanGroupCount = 0,
                                   // Everything about this bundle that says the package cache
                                   // could not serve the run: DependencyResolver's unservable
                                   // dependencies, plus platform runtime apps the dependency load
                                   // found symbol-only. Each is printed once at discovery time,
                                   // ~20s into a run that then spends minutes compiling — so
                                   // PrintSummary repeats them at the end, where a scripted caller
                                   // and a human scrolling to the bottom actually look (#2587).
                                   // Optional/trailing for the same reason as RanGroupCount.
                                   IReadOnlyList<string>? ProvisionGaps = null);

public static class Reporter
{
    /// <summary>
    /// A bucket that RAN but lost one or more suites on the way (#2762). Every surface that
    /// reports a result asks this same question, so it is asked in one place — a marker that
    /// meant different things in the summary and in --output-json would be worse than none.
    /// </summary>
    internal static bool IsPartialLoss(BucketResult b)
        => b.Stage == BucketStage.Ran && b.CompileErrors.Count > 0;

    /// <summary>
    /// Whether one result is COLLATERAL-SUSPECT: a fail/error that shares a bucket with a suite
    /// error (#2880).
    ///
    /// <para>The incident: a runner-extras run lost 23 suites to one module that did not compile
    /// and reported <c>187 total, pass: 180, fail: 7</c>. All 7 FAILs were in suites that
    /// survived, and all 7 were caused by objects the lost module declared — query joins, a role
    /// centre, an out-of-scope classification. Re-run on the identical tree and build the same
    /// suite was 265/265 green. Nothing distinguished those 7 from a real regression, and seven
    /// substantive-looking failures in unrelated areas is seven issues nobody should file.</para>
    ///
    /// <para>Passes are deliberately NOT marked. A missing object manufactures failures; it does
    /// not manufacture passes, and marking a green result unverified would spread the doubt to
    /// results nothing threatens. Nor is the marker applied run-wide: only the bucket that
    /// actually lost a suite is implicated, so an intact bundle in the same run keeps reporting
    /// exactly what it measured.</para>
    /// </summary>
    internal static bool IsSuspect(BucketResult b, TestResult t)
        => IsPartialLoss(b)
           && t.Outcome is TestOutcome.Fail or TestOutcome.Error
           && !IsNamedCauseOfASuiteError(b, t);

    /// <summary>
    /// Whether this result is the CAUSE of one of the bucket's suite errors rather than one of
    /// its casualties (#2880 review). In general the runner cannot tell the two apart — a module
    /// that did not compile names no test. The watchdog case is the exception: the abort reason
    /// names the codeunit and method that hung, and that result is the one thing in the run that
    /// is definitely real. Marking it unverified would point the reader away from the only
    /// genuine problem, which is the same argument that already keeps the <c>"kind": "suite"</c>
    /// record in --out unmarked.
    ///
    /// <para>Both sides of the string go through
    /// <see cref="Infrastructure.BundleFailureStage.AbortReasonHead"/>, so a reworded abort
    /// reason cannot silently stop the exclusion applying without failing its round-trip test.
    /// If it ever did, the failure mode is over-marking — the pre-review behaviour — not a real
    /// failure hidden.</para>
    /// </summary>
    internal static bool IsNamedCauseOfASuiteError(BucketResult b, TestResult t)
        => b.CompileErrors.Any(e =>
            Infrastructure.BundleFailureStage.AbortReasonNamesTest(e, t.Codeunit, t.Method));

    /// <summary>Whether anything in this bucket is marked — i.e. whether the notes that
    /// introduce the marker have anything below them to introduce.</summary>
    internal static bool HasSuspects(BucketResult b) => b.Tests.Any(t => IsSuspect(b, t));

    /// <summary>The inline marker, e.g. <c>[suspect — bucket lost 23 suite(s)]</c>.</summary>
    internal static string SuspectMarker(BucketResult b)
        => $"[suspect — bucket lost {b.CompileErrors.Count} suite(s)]";

    /// <summary>
    /// Totals carried in from earlier attempts of the same run (#2280). A watchdog resume runs
    /// only the codeunits no attempt has reached, so its own results are a PARTIAL view — without
    /// these the final summary would report the last attempt's slice as if it were the whole run,
    /// which is a smaller number stated with more confidence than the truth.
    /// </summary>
    public readonly record struct CarriedTotals(int Tests, int Pass, int Fail, int Error)
    {
        public static CarriedTotals operator +(CarriedTotals a, CarriedTotals b)
            => new(a.Tests + b.Tests, a.Pass + b.Pass, a.Fail + b.Fail, a.Error + b.Error);
        public bool IsEmpty => Tests == 0 && Pass == 0 && Fail == 0 && Error == 0;
    }

    public static void PrintSummary(IReadOnlyList<BucketResult> buckets, TextWriter w)
        => PrintSummary(buckets, w, default);

    public static void PrintSummary(IReadOnlyList<BucketResult> buckets, TextWriter w, CarriedTotals carried)
    {
        int totalTests = 0, pass = 0, fail = 0, err = 0, skipped = 0;
        int passOos = 0, passKnownGap = 0, passDivergence = 0;
        int compileFailed = 0, execFailed = 0;
        // #2762: buckets that RAN but lost one or more suites on the way. Their Stage stays
        // `Ran` — one surviving sibling suite is enough — so neither counter above sees them,
        // and every number printed below describes only what survived. computedExitCode in
        // Program.cs already fails the run for exactly this condition; without these the
        // summary contradicted it in silence.
        var partialBuckets = new List<BucketResult>();
        int lostSuites = 0;
        // #2880: how many of the fail/error results below share a bucket with a suite error.
        int suspect = 0;
        // …and how many suites those buckets lost. NOT `lostSuites`: with one bucket losing 23
        // suites but passing everything and another losing 1 and failing twice, the run-wide
        // total would make this line say "lost 24 suite(s)" while every per-result marker below
        // said "bucket lost 1 suite(s)" — one marker quoting two different numbers, which is
        // the defect the single IsSuspect predicate exists to prevent, one level up.
        int suspectLostSuites = 0;
        TimeSpan emit = TimeSpan.Zero, comp = TimeSpan.Zero, run = TimeSpan.Zero;
        foreach (var b in buckets)
        {
            emit += b.EmitTime; comp += b.CompileTime; run += b.RunTime;
            if (b.Stage == BucketStage.CompileFailed) { compileFailed++; continue; }
            if (b.Stage == BucketStage.ExecuteFailed) { execFailed++; continue; }
            if (b.CompileErrors.Count > 0)
            {
                partialBuckets.Add(b);
                lostSuites += b.CompileErrors.Count;
            }
            if (HasSuspects(b)) suspectLostSuites += b.CompileErrors.Count;
            foreach (var t in b.Tests)
            {
                if (IsSuspect(b, t)) suspect++;
                totalTests++;
                if (t.Outcome == TestOutcome.Pass)
                {
                    pass++;
                    if (t.Expectation == Infrastructure.ExpectationResult.PassOos) passOos++;
                    else if (t.Expectation == Infrastructure.ExpectationResult.PassKnownGap) passKnownGap++;
                    else if (t.Expectation == Infrastructure.ExpectationResult.PassDivergence) passDivergence++;
                }
                else if (t.Outcome == TestOutcome.Fail) fail++;
                else if (t.Outcome == TestOutcome.Skipped) skipped++;
                else err++;
            }
        }
        w.WriteLine();
        w.WriteLine("=================================================================");
        w.WriteLine("al-runner — test run summary");
        w.WriteLine("=================================================================");
        w.WriteLine($"Buckets:       {buckets.Count} total");
        w.WriteLine($"  ran:         {buckets.Count - compileFailed - execFailed}");
        w.WriteLine($"  compile-fail:{compileFailed}");
        w.WriteLine($"  exec-fail:   {execFailed}");
        // Deliberately NOT folded into `compile-fail`: these buckets really did run and their
        // surviving results are real, so calling them compile failures would misstate the run
        // in the other direction. Omitted entirely when there is nothing to say, so a clean
        // run's summary is byte-identical to before.
        if (partialBuckets.Count > 0)
            w.WriteLine($"  partial:     {partialBuckets.Count}  (ran, but {lostSuites} suite(s) "
                + "did not — see \"Suite errors\" below)");
        if (!carried.IsEmpty)
        {
            // Named rather than folded in silently: these tests ran in an EARLIER process of this
            // same run, before a watchdog abort forced a resume. A reader who cannot see that
            // cannot tell this total from a single clean run's.
            w.WriteLine($"  (carried from earlier attempt(s): {carried.Tests} tests, "
                + $"{carried.Pass} pass, {carried.Fail} fail, {carried.Error} error)");
            totalTests += carried.Tests; pass += carried.Pass; fail += carried.Fail; err += carried.Error;
        }
        w.WriteLine($"Tests:         {totalTests} total");
        w.WriteLine($"  pass:        {pass}");
        // Manifest reclassifications (docs/expectations.md) are surfaced DISTINCTLY so
        // a green run that got there via quarantined tests does not read as an
        // unqualified green. Zero-count lines are omitted: no manifest, no noise.
        if (passOos > 0)
            w.WriteLine($"    pass-oos:        {passOos}");
        if (passKnownGap > 0)
            w.WriteLine($"    pass-known-gap:  {passKnownGap}");
        if (passDivergence > 0)
            w.WriteLine($"    pass-divergence: {passDivergence}");
        w.WriteLine($"  fail:        {fail}");
        w.WriteLine($"  error:       {err}");
        if (skipped > 0)
            w.WriteLine($"  skipped:     {skipped}");
        // #2880: the single number the incident log did not have. `fail: 7` sitting next to
        // `partial: 1` leaves the reader to work out whether the 7 have anything to do with the
        // 1 — and the answer, that time, was "all of them". Printed only when there is something
        // to say: `suspect: 0` on every clean run trains the reader to skip the line on exactly
        // the runs where it matters.
        if (suspect > 0)
        {
            w.WriteLine($"  suspect:     {suspect}  — these fail/error results are in bucket(s) "
                + $"that also lost {suspectLostSuites} suite(s).");
            w.WriteLine("               A missing object, or state an aborted suite left "
                + "behind, makes unrelated tests");
            w.WriteLine("               fail — so treat them as UNVERIFIED: fix the suite "
                + "errors below and re-run first.");
        }
        w.WriteLine($"Time:");
        // Invariant($) throughout the numeric output below: #2968. An interpolated `:F1` or
        // `:N0` formats with the AMBIENT culture, so a developer on a comma-decimal LANG read
        // `total: 7,5s` while CI read `7.5s` — and the summary is machine-read (asserted
        // literally in AlRunner.Tests, and the source of --output-json and the JUnit report).
        w.WriteLine(Invariant($"  AL emit:     {emit.TotalSeconds:F1}s"));
        w.WriteLine(Invariant($"  C# compile:  {comp.TotalSeconds:F1}s"));
        w.WriteLine(Invariant($"  test run:    {run.TotalSeconds:F1}s"));
        w.WriteLine(Invariant($"  total:       {(emit + comp + run).TotalSeconds:F1}s"));
        // #1936: `total:` above is only emit+compile+run — it does NOT include the
        // per-process fixed costs paid before any of those phases start (BC runtime
        // patch application, dependency/package-cache indexing, install-seed-dep
        // company baseline, etc. — see COMMON.md's boot-overhead profile). A warm run
        // of a single-test fixture can report "total: 6.3s" while the process actually
        // took ~23s wall clock, which reads as a lie to anyone timing the CLI from the
        // outside. `wall:` is the real process wall-clock — OS process start time to
        // this print — so the two numbers together show both "how long the phases we
        // measure took" and "how long the process actually took", instead of only the
        // former pretending to be the latter.
        var wall = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        w.WriteLine(Invariant($"  wall:        {wall.TotalSeconds:F1}s"));
        // #2262: --test-data loads a table on first touch, so its outcome is only complete
        // once the run is. Under the eager policy this line was printed by the provisioner
        // itself, before any test ran; there is no such moment any more. Absent the flag
        // (or with it, if nothing the suite touched was in the backup) there is no summary
        // and nothing is printed, so a default run's output is unchanged.
        var testData = AlRunner.TestDataProvisioner.LastSummary;
        if (testData != null)
            w.WriteLine(testData.Describe());
        // A dependency no loader tier can serve is reported once, per bundle, on stderr at
        // dependency-resolution time — then the run spends minutes compiling and says nothing
        // more about it. Measured on npcore: four such blocks at ~20s, 212s of emit and compile,
        // and a failure whose message was exactly what those blocks predicted, with ~2,600 lines
        // of log in between. This is the part a scripted caller and a human scrolling to the
        // bottom actually read, so it repeats them verbatim — each block is what names the app,
        // the winning path and the fix command.
        //
        // Only when there ARE gaps: a section printed on every run is noise, and every
        // integration test asserting on the markers above would then be asserting past it.
        // #2762: the same argument as the provisioning-gap block below, for a louder failure.
        // The EMIT-EXCLUDED / EMIT-ZERO / COMPILE-FAIL line that explains a lost suite is
        // written on stderr at the moment it happens — tens to thousands of lines above this
        // point on a real run — and the numbers printed just above it read as a clean pass.
        // Repeat each message VERBATIM: it is what names the surface (EMIT-EXCLUDED), the
        // module and the objects that were dropped, and a paraphrase would send the reader
        // back up the log (.claude/rules/loud-failures.md).
        if (partialBuckets.Count > 0)
        {
            w.WriteLine("-----------------------------------------------------------------");
            w.WriteLine($"Suite errors: {lostSuites} — in {partialBuckets.Count} bucket(s) that "
                + "otherwise ran. Every test these suites declare is MISSING from the counts "
                + "above, so this run covers less than it discovered.");
            foreach (var b in partialBuckets)
            {
                w.WriteLine(b.BucketPath);
                foreach (var e in b.CompileErrors) w.WriteLine($"  {e}");
            }
        }
        // #2764, and the same argument as the "Suite errors" block directly above, for the case
        // one step further along: a bucket that did not compile AT ALL contributes no tests, so
        // every number in the Tests block is byte-identical to a clean run of whatever else ran.
        // `compile-fail:` does move, but the line naming WHICH bundle and WHY was written when it
        // happened — tens to thousands of lines above this point — and PrintPerTest's
        // "=== X — COMPILE FAIL ===" block sits up there with it.
        //
        // In a one-shot run the exit code still catches it. Under --watch there is no exit code
        // at all: this summary IS the interface, nobody scrolls a live session, and the faster
        // the loop the more it is trusted. Deliberately the same shape as the block above rather
        // than a second spelling of it — name the bucket, repeat its errors VERBATIM (a
        // paraphrase sends the reader back up the log), and print nothing at all when there is
        // nothing to say, so a clean run's summary is unchanged.
        var failedBuckets = buckets.Where(b => b.Stage == BucketStage.CompileFailed).ToList();
        if (failedBuckets.Count > 0)
        {
            w.WriteLine("-----------------------------------------------------------------");
            w.WriteLine($"Compile failures: {failedBuckets.Count} bucket(s) did not compile, so "
                + "NONE of the tests they declare appear in the counts above — this run covers "
                + "less than it discovered.");
            foreach (var b in failedBuckets)
            {
                w.WriteLine(b.BucketPath);
                foreach (var e in b.CompileErrors) w.WriteLine($"  {e}");
                if (b.ProcessError != null) w.WriteLine($"  {b.ProcessError}");
            }
        }
        // #2831, the sibling of the block above for a bucket that BUILT and LOADED and then
        // failed to run. Same silence — no tests, so the Tests block reads as a clean run —
        // and reproducible: `--test-data=<a file that is not a SQL Server backup>` gives
        // `exec-fail: 1` with `Tests: 0 total`, every AL object having compiled cleanly.
        //
        // A SEPARATE heading rather than sharing "Compile failures", deliberately. #2779 exists
        // because exactly this conflation one layer down reported that bundle as
        // `compile-fail: 1`, and "the reader of that report goes looking for AL compile errors
        // that do not exist". The two failures need different next actions: a compile failure
        // says look at your AL, an execution failure says the module was fine and something
        // around it died. Merging them here would put that mis-direction back, in the block a
        // developer actually reads.
        //
        // ProcessError first when present: the out-of-process fan-out path carries its reason
        // there rather than in CompileErrors, and a bucket whose worker died has nothing in the
        // latter at all.
        var deadBuckets = buckets.Where(b => b.Stage == BucketStage.ExecuteFailed).ToList();
        if (deadBuckets.Count > 0)
        {
            w.WriteLine("-----------------------------------------------------------------");
            w.WriteLine($"Execution failures: {deadBuckets.Count} bucket(s) compiled and loaded and "
                + "then failed to run, so NONE of the tests they declare appear in the counts above "
                + "— this run covers less than it discovered. The AL itself built cleanly.");
            foreach (var b in deadBuckets)
            {
                w.WriteLine(b.BucketPath);
                if (b.ProcessError != null) w.WriteLine($"  {b.ProcessError}");
                foreach (var e in b.CompileErrors) w.WriteLine($"  {e}");
            }
        }
        var gaps = buckets
            .SelectMany(b => b.ProvisionGaps ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (gaps.Count > 0)
        {
            w.WriteLine("-----------------------------------------------------------------");
            // Counts them and gets out of the way. Two sources feed this and their consequences
            // differ — an unservable dependency really does fail every call into it, while a
            // symbol-only platform app falls back to service-tier DLL dispatch and often works.
            // A heading asserting either one is wrong for the other half, and each block already
            // carries its own consequence and its own fix command.
            w.WriteLine($"Provisioning gaps: {gaps.Count} — the package cache could not fully serve this run.");
            foreach (var g in gaps) w.WriteLine(g);
        }
        w.WriteLine("=================================================================");
    }

    // V1-style per-test output. By default prints only FAIL and ERROR entries (PASS is too
    // noisy at thousands of tests). `showPass=true` adds PASS lines for parity with V1's
    // `PASS  Codeunit.Method (Nms)` format.
    public static void PrintPerTest(IReadOnlyList<BucketResult> buckets, TextWriter w, bool showPass)
    {
        foreach (var b in buckets)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                w.WriteLine();
                w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} — COMPILE FAIL ===");
                foreach (var e in b.CompileErrors.Take(20)) w.WriteLine($"  {e}");
                if (b.CompileErrors.Count > 20)
                    w.WriteLine($"  ... and {b.CompileErrors.Count - 20} more compile errors");
                continue;
            }
            if (b.Stage == BucketStage.ExecuteFailed)
            {
                w.WriteLine();
                w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} — EXEC FAIL ===");
                if (b.ProcessError != null) w.WriteLine($"  {b.ProcessError}");
                // #2779: an in-process bundle that failed at RUN time has no ProcessError — its
                // diagnosis is in the suite-error list, the same list the COMPILE FAIL branch
                // above prints. Without this the header was the ONLY thing printed and the
                // reason vanished entirely, which is the defect this stage split would
                // otherwise have introduced while fixing the wrong header.
                foreach (var e in b.CompileErrors.Take(20)) w.WriteLine($"  {e}");
                if (b.CompileErrors.Count > 20)
                    w.WriteLine($"  ... and {b.CompileErrors.Count - 20} more errors");
                continue;
            }
            // #2762: a bucket that ran but lost suites. This MUST come before the
            // `visible.Count == 0` skip below: at default verbosity a bucket whose survivors
            // all passed has nothing visible, which is precisely the all-green run where a
            // silently missing suite does the most damage.
            if (b.CompileErrors.Count > 0)
            {
                w.WriteLine();
                w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} — SUITE ERRORS ({b.CompileErrors.Count}) ===");
                foreach (var e in b.CompileErrors.Take(20)) w.WriteLine($"  {e}");
                if (b.CompileErrors.Count > 20)
                    w.WriteLine($"  ... and {b.CompileErrors.Count - 20} more suite errors");
                w.WriteLine("  → the tests these suites declare are MISSING from this run, not passing.");
                // #2880: and the half that is easy to miss — the tests that DID run in this
                // bucket can fail because of what the lost suites took with them. Said here, on
                // the way in to the results, so the reader meets the caveat before the failures.
                //
                // Only when there is something below to mark. A partial bucket whose survivors
                // all passed has no marked result, and `visible.Count == 0` two lines down then
                // skips the section entirely — so an ungated note announced a convention for
                // results that were never printed.
                if (HasSuspects(b))
                    w.WriteLine("  → FAIL/ERROR results below are marked "
                        + $"{SuspectMarker(b)}: a missing object, or state an aborted suite left "
                        + "behind, makes unrelated tests fail — so they are UNVERIFIED until the "
                        + "suite error above is fixed and the run repeats.");
            }
            // Skip silent buckets — only emit a per-bucket header if there's anything to show.
            var visible = b.Tests.Where(t => showPass || t.Outcome != TestOutcome.Pass).ToList();
            if (visible.Count == 0) continue;
            w.WriteLine();
            w.WriteLine($"=== {Path.GetFileName(b.BucketPath)} ===");
            foreach (var t in visible)
            {
                var label = t.Outcome switch
                {
                    TestOutcome.Pass when t.Expectation == Infrastructure.ExpectationResult.PassOos => "PASS (oos)",
                    TestOutcome.Pass when t.Expectation == Infrastructure.ExpectationResult.PassKnownGap => "PASS (known-gap)",
                    TestOutcome.Pass when t.Expectation == Infrastructure.ExpectationResult.PassDivergence => "PASS (divergence)",
                    TestOutcome.Pass => "PASS ",
                    TestOutcome.Fail => "FAIL ",
                    TestOutcome.Error => "ERROR",
                    TestOutcome.Skipped => "SKIP ",
                    _ => "?    "
                };
                long ms = (long)t.Duration.TotalMilliseconds;
                // Appended, never substituted into the prefix: ParallelFanOut and every other
                // consumer matches on the leading label and the `=== <bundle> ===` header, so a
                // suffix is additive where a reformat would silently change what they count.
                var suspectSuffix = IsSuspect(b, t) ? "  " + SuspectMarker(b) : "";
                w.WriteLine($"{label} {t.Codeunit}.{t.Method} ({ms}ms){suspectSuffix}");
                if (t.Outcome != TestOutcome.Pass)
                {
                    if (!string.IsNullOrEmpty(t.Message))
                        w.WriteLine($"      {t.Message}");
                    // #2240: printed AFTER BC's own message and BEFORE the AL stack, so the
                    // failure still reads as BC reported it and the explanation sits next to it
                    // rather than in place of it. Absent evidence there is no line at all, which
                    // is why a default run's output is unchanged.
                    if (!string.IsNullOrEmpty(t.Diagnosis))
                        w.WriteLine($"      {t.Diagnosis}");
                    if (!string.IsNullOrEmpty(t.AlCallStack))
                    {
                        // Show the AL call stack (BC service-tier format), not the C# trace.
                        foreach (var frame in t.AlCallStack.Split('\n'))
                            w.WriteLine($"      {frame.TrimEnd('\r')}");
                    }
                    else if (!string.IsNullOrEmpty(t.FullException))
                    {
                        foreach (var line in FilteredStack(t.FullException, max: 8))
                            w.WriteLine($"      {line}");
                    }
                }
            }
        }
    }

    // Failure-classification summary — groups all failing tests by ClassifyTest output and
    // ranks descending by count. This is the "where to attack next" view: each line is a
    // cluster of failures that one runtime/API fix could potentially resolve in bulk.
    public static void PrintFailureClassification(IReadOnlyList<BucketResult> buckets,
        TextWriter w, int topN = 25)
    {
        var failing = buckets
            .Where(b => b.Stage == BucketStage.Ran)
            .SelectMany(b => b.Tests
                .Where(t => t.Outcome is TestOutcome.Fail or TestOutcome.Error)
                .Select(t => (Bucket: b, Test: t)))
            .ToList();
        // #2880: this view ranks failure clusters and reads as a work list — it is the surface
        // most likely to turn collateral damage into filed issues. Stated BEFORE the ranking,
        // because the ranking is what the reader acts on.
        int suspect = failing.Count(x => IsSuspect(x.Bucket, x.Test));
        var groups = failing
            .Select(x => x.Test)
            .GroupBy(t => ClassifyTest(t.Message ?? "", t.FullException ?? ""))
            .Select(g => (Classification: g.Key, Count: g.Count(),
                          Sample: g.First()))
            .OrderByDescending(g => g.Count)
            .ToList();
        if (groups.Count == 0) return;
        w.WriteLine();
        w.WriteLine($"=== Failures by classification (top {Math.Min(topN, groups.Count)} of {groups.Count}) ===");
        int totalFail = groups.Sum(g => g.Count);
        if (suspect > 0)
            w.WriteLine($"  NOTE: {suspect} of {totalFail} are suspect — in bucket(s) that lost "
                + "suites. A cluster made entirely of those is not a bug to attack; it is the "
                + "suite error, seen from downstream.");
        foreach (var g in groups.Take(topN))
        {
            double pct = 100.0 * g.Count / totalFail;
            w.WriteLine(Invariant($"  {g.Count,6:N0}  {pct,5:F1}%  {g.Classification}"));
        }
        if (groups.Count > topN)
        {
            int tailCount = groups.Skip(topN).Sum(g => g.Count);
            double tailPct = 100.0 * tailCount / totalFail;
            w.WriteLine(Invariant($"  {tailCount,6:N0}  {tailPct,5:F1}%  ... and {groups.Count - topN} more classifications"));
        }
    }

    // Strips noise from a .NET exception's stack trace — keeps frames that mention
    // user/MS BC types, drops async/await internals, runtime infra, and our own patch
    // dispatch frames. Returns up to `max` lines.
    private static IEnumerable<string> FilteredStack(string fullException, int max)
    {
        var noise = new[] {
            "System.Runtime.CompilerServices",
            "System.Threading.Tasks.Task",
            "MoveNext()",
            "System.Runtime.ExceptionServices",
            "AlRunner.Patches.AsyncStateMachineSpike",
            "AlRunner.Infrastructure.JmpHook",
        };
        int kept = 0;
        foreach (var raw in fullException.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("at ") == false) continue;
            if (noise.Any(n => line.Contains(n))) continue;
            yield return line.TrimStart();
            if (++kept >= max) yield break;
        }
    }

    // v1-shaped per-test JSON to stdout (--output-json), distinct from
    // WriteClassification's failure-classification file (--out). capturedValues/
    // iterations fields from v1 are intentionally omitted — those need a shared
    // Cecil-instrumentation prerequisite that doesn't exist yet, tracked separately.
    public static string SerializeJsonOutput(IReadOnlyList<BucketResult> buckets, int exitCode)
    {
        // Paired with its bucket rather than flattened: `suspect` is a property of the
        // (bucket, test) pair, and this document flattens `tests` across buckets so a consumer
        // cannot recover the pairing from `suiteErrors` afterwards (#2880).
        var tests = buckets
            .Where(b => b.Stage == BucketStage.Ran)
            .SelectMany(b => b.Tests.Select(t => (Bucket: b, Test: t)))
            .ToList();

        // One entry per compile-failed bucket, identified by its FULL path. This used to
        // build a Dictionary keyed on Path.GetFileName(BucketPath), which threw an
        // unhandled ArgumentException ("An item with the same key has already been added")
        // whenever two bundles shared a last path segment — `al-runner ./appA/src
        // ./appB/src`, or the same directory passed twice. That fired here, during report
        // serialisation and AFTER the tests had already run, so a completed run's results
        // were discarded with a raw stack trace. The dictionary bought nothing: the result
        // is projected straight back into an array. Full paths also keep same-named
        // bundles distinguishable in the output — same field name as WriteClassification's
        // `bucket`. See #1692.
        var compileErrors = buckets
            .Where(b => b.Stage == BucketStage.CompileFailed)
            .Select(b => new { file = b.BucketPath, errors = b.CompileErrors.ToList() })
            .ToList();

        // #2779: an ExecuteFailed bucket used to appear NOWHERE in this document — not in
        // `tests` (it has none), not in `compilationErrors` (it is not compile-failed). A
        // consumer reading `--json` saw a run that silently lost a whole bundle. Additive and
        // null-omitted, so a run with no execution failure serialises byte-identically.
        var executionErrors = buckets
            .Where(b => b.Stage == BucketStage.ExecuteFailed)
            .Select(b => new
            {
                file = b.BucketPath,
                errors = b.ProcessError is { } pe
                    ? b.CompileErrors.Prepend(pe).ToList()
                    : b.CompileErrors.ToList(),
            })
            .ToList();

        // #2762: exactly the gap #2779 closed for ExecuteFailed buckets, one Stage over. A
        // bucket that ran but lost a suite is not compile-failed, so it appeared in neither
        // `compilationErrors` nor `tests` — a consumer read a fully passing document next to
        // `exitCode: 3` and had nothing to explain it. Additive and null-omitted, so a run
        // that lost nothing serialises byte-identically.
        var suiteErrors = buckets
            .Where(b => b.Stage == BucketStage.Ran && b.CompileErrors.Count > 0)
            .Select(b => new { file = b.BucketPath, errors = b.CompileErrors.ToList() })
            .ToList();

        var output = new
        {
            tests = tests.Select(x => new
            {
                name = $"{x.Test.Codeunit}.{x.Test.Method}",
                status = x.Test.Outcome.ToString().ToLowerInvariant(),
                durationMs = (long)x.Test.Duration.TotalMilliseconds,
                message = x.Test.Message,
                // #2240: additive and null-omitted (DefaultIgnoreCondition below), so a run that
                // produced no diagnosis emits byte-identical JSON to before.
                diagnosis = x.Test.Diagnosis,
                stackTrace = (x.Test.AlCallStack ?? x.Test.FullException)?.TrimEnd(),
                // #2880: `true` on a fail/error that shares a bucket with a suite error — the
                // result may be collateral damage from objects the lost suite took with it.
                // Nullable and null-omitted, so a clean run's document is byte-identical and a
                // consumer reading this field gets `true` or nothing, never a misleading
                // `false` on a green result.
                suspect = IsSuspect(x.Bucket, x.Test) ? true : (bool?)null,
                // Manifest reclassification (docs/expectations.md): "pass-oos",
                // "pass-known-gap", "pass-divergence", "skipped" or
                // "fail-manifest-drift". Omitted (null) for results the manifest
                // did not touch.
                expectation = x.Test.Expectation switch
                {
                    Infrastructure.ExpectationResult.PassOos => "pass-oos",
                    Infrastructure.ExpectationResult.PassKnownGap => "pass-known-gap",
                    Infrastructure.ExpectationResult.PassDivergence => "pass-divergence",
                    Infrastructure.ExpectationResult.Skipped => "skipped",
                    Infrastructure.ExpectationResult.FailManifestDrift => "fail-manifest-drift",
                    _ => null,
                },
            }),
            passed = tests.Count(x => x.Test.Outcome == TestOutcome.Pass),
            failed = tests.Count(x => x.Test.Outcome == TestOutcome.Fail),
            errors = tests.Count(x => x.Test.Outcome == TestOutcome.Error),
            skipped = tests.Count(x => x.Test.Outcome == TestOutcome.Skipped),
            total = tests.Count,
            exitCode,
            compilationErrors = compileErrors.Count > 0 ? compileErrors : null,
            executionErrors = executionErrors.Count > 0 ? executionErrors : null,
            suiteErrors = suiteErrors.Count > 0 ? suiteErrors : null,
            // #1936: same "real wall clock, not just the measured phases" gap as the
            // `wall:` line in PrintSummary — see that comment. Additive field, so
            // existing consumers reading this JSON are unaffected.
            wallSeconds = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds,
        };

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    public static void WriteClassification(IReadOnlyList<BucketResult> buckets, string path)
    {
        var failures = new List<object>();
        foreach (var b in buckets)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                failures.Add(new
                {
                    bucket = b.BucketPath,
                    kind = "compile",
                    errors = b.CompileErrors.Take(10).ToList(),
                    classification = ClassifyCompile(b.CompileErrors),
                });
            }
            else if (b.Stage == BucketStage.ExecuteFailed)
            {
                // #2779: `ProcessError` is set only by the out-of-process fan-out path. An
                // in-process bundle that failed at RUN time carries its diagnosis in the same
                // suite-error list a compile failure uses, so emitting only `error` here would
                // have written `"error": null` — a failure record naming nothing. Both fields
                // are emitted, and the classification comes from the errors' own markers rather
                // than asserting a child process died when none did.
                failures.Add(new
                {
                    bucket = b.BucketPath,
                    kind = "execute",
                    error = b.ProcessError,
                    errors = b.CompileErrors.Take(10).ToList(),
                    classification = ClassifyExecute(b),
                });
            }
            else
            {
                // #2762: this branch walked only failing TESTS, so a bucket that lost a whole
                // suite contributed zero records and the triage file said the run had nothing
                // wrong with it. Its own kind, not "compile": the bucket ran, and the records
                // below it are still that bucket's real test failures.
                if (b.CompileErrors.Count > 0)
                {
                    failures.Add(new
                    {
                        bucket = b.BucketPath,
                        kind = "suite",
                        errors = b.CompileErrors.Take(10).ToList(),
                        classification = ClassifyCompile(b.CompileErrors),
                    });
                }
                foreach (var t in b.Tests.Where(t => t.Outcome is TestOutcome.Fail or TestOutcome.Error))
                {
                    failures.Add(new
                    {
                        bucket = b.BucketPath,
                        kind = t.Outcome.ToString().ToLowerInvariant(),
                        codeunit = t.Codeunit,
                        method = t.Method,
                        message = t.Message,
                        // #2880: this file is the triage worklist a follow-up pass reads. A
                        // collateral failure in it is a work item nobody should start. Always
                        // emitted, unlike --output-json's null-omitted field: this document does
                        // not suppress nulls, and a consumer filtering `suspect == false` needs
                        // the field present on the records it keeps. The `"kind": "suite"` record
                        // above is deliberately NOT marked — it is the cause, and calling it
                        // unverified would point the reader away from the only real problem here.
                        suspect = IsSuspect(b, t),
                        diagnosis = t.Diagnosis,
                        // First few stack frames (after the test method) — enough to identify
                        // which BC API the failure hit, but not so many that the JSON explodes.
                        stack_top = StackTop(t.FullException, 6),
                        classification = ClassifyTest(t.Message ?? "", t.FullException ?? ""),
                    });
                }
            }
        }
        var grouped = failures
            .GroupBy(f => f.GetType().GetProperty("classification")!.GetValue(f) as string ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => new { classification = g.Key, count = g.Count(), examples = g.Take(3).ToList() })
            .ToList();
        var doc = new
        {
            generated = DateTime.UtcNow.ToString("o"),
            total_failures = failures.Count,
            classifications = grouped,
            all_failures = failures,
        };
        File.WriteAllText(path,
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IReadOnlyList<string> StackTop(string? full, int max)
    {
        if (string.IsNullOrEmpty(full)) return Array.Empty<string>();
        return full.Split('\n')
            .Where(l => l.TrimStart().StartsWith("at "))
            .Take(15)  // more frames for diagnosis
            .Select(l => l.Trim())
            .ToArray();
    }

    /// <summary>
    /// The classification for a bucket that failed at RUN time (#2779). `process-error` is kept
    /// for the out-of-process fan-out path, where a child process really did fail; an in-process
    /// bundle is named by its own error marker instead, so a reader can tell a thrown exception
    /// from codeunits abandoned by the per-test watchdog without parsing the message.
    /// </summary>
    private static string ClassifyExecute(BucketResult b)
    {
        if (b.ProcessError != null) return "process-error";
        var first = b.CompileErrors.FirstOrDefault();
        var marker = first == null ? null : Infrastructure.BundleFailureStage.MarkerOf(first);
        return marker == null ? "execute/other" : $"execute/{marker.ToLowerInvariant()}";
    }

    // Hand-tuned classification heuristics — purely descriptive, not authoritative.
    private static string ClassifyCompile(IReadOnlyList<string> errors)
    {
        var first = errors.FirstOrDefault() ?? "";
        if (first.Contains("CS0246") || first.Contains("CS0103")) return "compile/missing-type-or-name";
        if (first.Contains("CS0117")) return "compile/missing-member";
        if (first.Contains("CS1503") || first.Contains("CS1501")) return "compile/signature-mismatch";
        if (first.Contains("CS0029") || first.Contains("CS0030")) return "compile/conversion";
        if (first.Contains("CS0234")) return "compile/missing-namespace";
        return "compile/other";
    }

    private static string ClassifyTest(string message, string full)
    {
        // Out-of-scope failures (loud-failures.md / docs/scope.md) are classified
        // by API name, not by stack frame — contract surface, not an NRE.
        // The convention is parsed in exactly one place, Infrastructure.
        // OutOfScopeMessage, which the expectations manifest reads too (#1743).
        if (Infrastructure.OutOfScopeMessage.TryParse(message, out var oosSignal)
            || Infrastructure.OutOfScopeMessage.TryParse(full, out oosSignal))
        {
            return oosSignal.Reason.Length == 0
                ? "out-of-scope/unknown"
                : $"out-of-scope/{oosSignal.Api}";
        }

        // Classify by the FIRST (innermost) BC stack frame — that's where the actual NRE
        // originates. Looking anywhere in the stack mis-buckets every AL test as
        // NavMethodScope because every AL method body wraps in a NavMethodScope.
        var first = full.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at Microsoft.Dynamics.Nav."));
        if (first != null)
        {
            // Strip "at Microsoft.Dynamics.Nav.<Group>." prefix and the parameter list.
            var cleaned = first;
            int paren = cleaned.IndexOf('(');
            if (paren > 0) cleaned = cleaned[..paren];
            cleaned = cleaned.Replace("at Microsoft.Dynamics.Nav.", "");
            return $"runtime/{cleaned}";
        }
        // Fallbacks for non-BC frames or empty stacks.
        if (message.Contains("MissingMethodException")) return "runtime/missing-method";
        if (message.Contains("MissingFieldException")) return "runtime/missing-field";
        if (message.Contains("TypeInitializationException")) return "runtime/cctor";
        if (message.Contains("InvalidCastException")) return "runtime/cast";
        if (message.Contains("PlatformNotSupportedException")) return "runtime/platform-not-supported";
        if (message.Contains("NullReferenceException")) return "runtime/null-deref";
        return "runtime/other";
    }
}
