// Reporter — aggregates per-test results into per-bucket and overall summaries,
// and writes a JSON failure-classification file for follow-up parallel work.
using System.Text.Json;
using static System.FormattableString;

namespace AlRunner;

public enum BucketStage { CompileFailed, ExecuteFailed, Ran }

/// <summary>
/// Company initialization (Base App codeunit 2 "Company-Initialize") aborted part-way, so the
/// company every test in this bucket ran against is missing whatever setup rows the codeunit
/// had not reached. Real BC cannot produce that state — codeunit 2's OnRun commits exactly
/// once, at the end — so this is a property of the RUN, not of any one test, and it is carried
/// on the bucket rather than left on stderr (#3538). See
/// docs/partial-company-initialization.md.
/// </summary>
public sealed record CompanyInitFailure(int CodeunitId, string CodeunitName,
                                        string ExceptionType, string Message,
                                        // #3561: how many app groups reported THIS abort. The
                                        // dependency-company-baseline carry re-reports on every
                                        // cache HIT — deliberately, each group did run against the
                                        // partial company — so N groups sharing one closure used to
                                        // print N identical lines. Collapsed at drain time by
                                        // Reporter.FinalizeCompanyInitFailures; 1 for an
                                        // uncollapsed abort, so an existing caller sees no change.
                                        int Count = 1,
                                        // #3561: non-null when the run's expectations manifest
                                        // declares this abort accepted (Mode =
                                        // accept-partial-company-init). The condition is still
                                        // printed and still recorded — only the exit-code
                                        // escalation is suppressed. Carries the entry's free-text
                                        // Reason, which is what the summary prints.
                                        string? AcceptedReason = null);

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
                                   // found symbol-only. Printed once per app by PrintActionNeeded,
                                   // at the end, where a scripted caller and a human scrolling to
                                   // the bottom actually look (#2587, #4560).
                                   // Optional/trailing for the same reason as RanGroupCount.
                                   IReadOnlyList<string>? ProvisionGaps = null,
                                   // #3538: company initialization aborted for one or more of
                                   // this bucket's app groups, so its tests ran against a
                                   // company real BC could not produce. Optional/trailing for
                                   // the same reason as the two above; null and empty both mean
                                   // "the company initialized cleanly", and every reporting
                                   // surface omits the condition entirely in that case, so a
                                   // clean run's output is byte-identical to before.
                                   IReadOnlyList<CompanyInitFailure>? CompanyInitFailures = null);

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

    /// <summary>
    /// Every company-initialization abort in the run, in bucket order (#3538). One place, so
    /// the summary, --out, --output-json and the JUnit report cannot describe the same
    /// condition differently — the reason IsSuspect exists one concept over.
    /// </summary>
    public static IReadOnlyList<CompanyInitFailure> CompanyInitFailures(IReadOnlyList<BucketResult> buckets)
        // #3561: collapsed ACROSS buckets as well as within one. The dependency-company baseline
        // is cached per dependency set, not per bundle, so the app groups re-reporting one abort
        // on a cache HIT are routinely in different buckets — collapsing only at the drain site
        // would still print one abort as N identical lines. --out gets only the WITHIN-bucket
        // collapse each BucketResult already carries, and no cross-bucket one: it is a per-bucket
        // triage worklist and each bucket's record belongs to that bucket.
        => Collapse(buckets.SelectMany(b => b.CompanyInitFailures ?? Array.Empty<CompanyInitFailure>()));

    /// <summary>
    /// Fold aborts that say the same thing into one record carrying the total app-group count.
    /// "The same thing" is the whole of what the accumulator records: codeunit id, codeunit
    /// name, exception type and message — see FinalizeCompanyInitFailures for why the key holds
    /// no app id (#3561).
    /// </summary>
    private static IReadOnlyList<CompanyInitFailure> Collapse(IEnumerable<CompanyInitFailure> failures)
    {
        var collapsed = new List<CompanyInitFailure>();
        var index = new Dictionary<(int, string, string, string), int>();
        foreach (var f in failures)
        {
            var key = (f.CodeunitId, f.CodeunitName, f.ExceptionType, f.Message);
            if (index.TryGetValue(key, out var at))
            {
                collapsed[at] = collapsed[at] with
                {
                    Count = collapsed[at].Count + f.Count,
                    // An acceptance is a property of the codeunit, so every member of a
                    // collapsed group carries the same answer; keeping the first non-null is
                    // just defensive about a group whose members were marked in different
                    // processes (a resumed run merges carried buckets).
                    AcceptedReason = collapsed[at].AcceptedReason ?? f.AcceptedReason,
                };
                continue;
            }
            index[key] = collapsed.Count;
            collapsed.Add(f);
        }
        return collapsed;
    }

    /// <summary>
    /// One abort, rendered the same way wherever it is printed. The two suffixes are #3561: how
    /// many app groups reported this same abort (omitted at 1), and the manifest reason that
    /// accepted it (omitted when nothing did). Both are part of the one rendering so the
    /// summary, the JUnit comment and a reader of either cannot see different facts.
    /// </summary>
    public static string DescribeCompanyInitFailure(CompanyInitFailure f)
        => $"codeunit {f.CodeunitId} \"{f.CodeunitName}\" did not complete: "
            + $"{f.ExceptionType}: {f.Message}"
            + (f.Count > 1 ? $" ×{f.Count} app group(s)" : "")
            + (f.AcceptedReason != null ? $" [accepted: {f.AcceptedReason}]" : "");

    /// <summary>
    /// Turn what the accumulator drained into what the run reports (#3561). Called from BOTH
    /// drain sites — the CLI bucket loop and the server per-request handler — so the two cannot
    /// describe the same condition differently.
    ///
    /// <para>Two things happen here. Identical aborts are COLLAPSED into one record carrying a
    /// count: N app groups sharing one dependency-company baseline each re-report the abort on
    /// their cache HIT (deliberately — each really did run against the partial company), which
    /// used to print N identical lines and emit N identical JSON entries. "Identical" is the
    /// whole of what the accumulator records: codeunit id, codeunit name, exception type and
    /// message. The app id is not part of the key because the accumulator holds no app id and
    /// the codeunit it records is always Base App's codeunit 2, so an app dimension would be
    /// constant today and invented rather than measured.</para>
    ///
    /// <para>And an abort the manifest ACCEPTS is marked with the entry's reason. That marking
    /// is the only thing that suppresses the exit 0 → 2 escalation in Program.cs; every
    /// reporting surface still carries the condition.</para>
    /// </summary>
    public static IReadOnlyList<CompanyInitFailure> FinalizeCompanyInitFailures(
        IReadOnlyList<CompanyInitFailure> drained, Infrastructure.ExpectationManifest? manifest)
    {
        if (drained.Count == 0) return Array.Empty<CompanyInitFailure>();
        return Collapse(drained.Select(f => f with
        {
            AcceptedReason = manifest?.FindCompanyInitAcceptance(f.CodeunitId, f.CodeunitName)?.Reason,
        }));
    }

    /// <summary>
    /// The aborts that make the run unclean (#3561): everything the manifest did not accept.
    /// The escalation reads this; every reporting surface reads the whole list.
    /// </summary>
    public static IReadOnlyList<CompanyInitFailure> UnacceptedCompanyInitFailures(
        IReadOnlyList<BucketResult> buckets)
        => CompanyInitFailures(buckets).Where(f => f.AcceptedReason == null).ToList();

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

    /// <summary>
    /// What the summary needs beyond the buckets (#4562). <see cref="Seed"/> null prints no seed
    /// line; <see cref="ReplayTarget"/> is the bundle argument(s) the replay command names.
    /// </summary>
    public sealed record SummaryOptions(bool Verbose = false, int? Seed = null, string? ReplayTarget = null);

    public static void PrintSummary(IReadOnlyList<BucketResult> buckets, TextWriter w)
        => PrintSummary(buckets, w, default, new SummaryOptions(Verbose: Log.Verbose));

    public static void PrintSummary(IReadOnlyList<BucketResult> buckets, TextWriter w, CarriedTotals carried)
        => PrintSummary(buckets, w, carried, new SummaryOptions(Verbose: Log.Verbose));

    public static void PrintSummary(IReadOnlyList<BucketResult> buckets, TextWriter w, CarriedTotals carried,
        SummaryOptions options)
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
        if (!carried.IsEmpty)
        {
            totalTests += carried.Tests; pass += carried.Pass; fail += carried.Fail; err += carried.Error;
        }
        var wall = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        w.WriteLine();
        // #4562: ONE counts line, machine-read (tools/preflight.py, bc-tests.yml's smoke step,
        // AlRunner.Tests). Every count is followed by at least one space, so `passed 1 ` never
        // matches `passed 10`. Reshape it only together with those readers.
        // Invariant($): #2968 — ambient-culture formatting printed `7,5 s` on a comma-decimal LANG.
        w.WriteLine(Invariant($"Tests: {totalTests}   passed {pass}   failed {fail}   errors {err}")
            + (skipped > 0 ? Invariant($"   skipped {skipped}") : "")
            + Invariant($"        Time: {(emit + comp + run).TotalSeconds:F1} s (wall {wall.TotalSeconds:F1} s)"));
        // Manifest reclassifications (docs/expectations.md) are surfaced DISTINCTLY so
        // a green run that got there via quarantined tests does not read as an
        // unqualified green. Zero-count lines are omitted: no manifest, no noise.
        if (passOos > 0)
            w.WriteLine($"    pass-oos:        {passOos}");
        if (passKnownGap > 0)
            w.WriteLine($"    pass-known-gap:  {passKnownGap}");
        if (passDivergence > 0)
            w.WriteLine($"    pass-divergence: {passDivergence}");
        if (!carried.IsEmpty)
            // Named rather than folded in silently: these tests ran in an EARLIER process of this
            // same run, before a watchdog abort forced a resume (#2280).
            w.WriteLine($"  (carried from earlier attempt(s): {carried.Tests} tests, "
                + $"{carried.Pass} pass, {carried.Fail} fail, {carried.Error} error)");
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
        // #4562: per-app rows only when an app did not run in full — otherwise they repeat the
        // counts line. Always under --verbose.
        if (options.Verbose || compileFailed > 0 || execFailed > 0 || partialBuckets.Count > 0)
        {
            w.WriteLine($"Apps:          {buckets.Count} total");
            w.WriteLine($"  ran:         {buckets.Count - compileFailed - execFailed}");
            w.WriteLine($"  compile-fail:{compileFailed}");
            w.WriteLine($"  exec-fail:   {execFailed}");
            // Deliberately NOT folded into `compile-fail`: these apps really did run and their
            // surviving results are real (#2762).
            if (partialBuckets.Count > 0)
                w.WriteLine($"  partial:     {partialBuckets.Count}  (ran, but {lostSuites} suite(s) "
                    + "did not — see \"Suite errors\" below)");
        }
        // #4562: the phase split behind --verbose — it reads `0.0s` on every cache hit.
        // `wall:` (#1936) is the process wall clock, so it covers boot costs `total:` does not.
        if (options.Verbose)
        {
            w.WriteLine("Time breakdown:");
            w.WriteLine(Invariant($"  AL emit:     {emit.TotalSeconds:F1}s"));
            w.WriteLine(Invariant($"  C# compile:  {comp.TotalSeconds:F1}s"));
            w.WriteLine(Invariant($"  test run:    {run.TotalSeconds:F1}s"));
            w.WriteLine(Invariant($"  total:       {(emit + comp + run).TotalSeconds:F1}s"));
            w.WriteLine(Invariant($"  wall:        {wall.TotalSeconds:F1}s"));
        }
        // #2262: --test-data loads a table on first touch, so its outcome is only complete
        // once the run is. Under the eager policy this line was printed by the provisioner
        // itself, before any test ran; there is no such moment any more. Absent the flag
        // (or with it, if nothing the suite touched was in the backup) there is no summary
        // and nothing is printed, so a default run's output is unchanged.
        var testData = AlRunner.TestDataProvisioner.LastSummary;
        if (testData != null)
            w.WriteLine(testData.Describe());
        // #2730: printed whenever --test-data-normalize-company is on, INCLUDING when no rule
        // fired. A normalization that happens silently is a measurement trap — someone compares
        // a normalized run against a recorded un-normalized one and reads it as the runner
        // having improved. Null (nothing printed) when the flag is off.
        var normalization = AlRunner.Infrastructure.TestDataNormalization.Describe();
        if (normalization != null)
            w.WriteLine(normalization);
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
            w.WriteLine($"Suite errors: {lostSuites} — in {partialBuckets.Count} app(s) that "
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
            w.WriteLine($"Compile failures: {failedBuckets.Count} app(s) did not compile, so "
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
            w.WriteLine($"Execution failures: {deadBuckets.Count} app(s) compiled and loaded and "
                + "then failed to run, so NONE of the tests they declare appear in the counts above "
                + "— this run covers less than it discovered. The AL itself built cleanly.");
            foreach (var b in deadBuckets)
            {
                w.WriteLine(b.BucketPath);
                if (b.ProcessError != null) w.WriteLine($"  {b.ProcessError}");
                foreach (var e in b.CompileErrors) w.WriteLine($"  {e}");
            }
        }
        // #3538: a run-level condition, printed as its own block rather than folded into the
        // counters above — none of them is about it. Every test below it ran against a company
        // real BC never produces, so this has to be readable next to the totals, not only as a
        // `[warn]` line thousands of lines earlier at the moment the codeunit aborted.
        var companyInitFailures = CompanyInitFailures(buckets);
        if (companyInitFailures.Count > 0)
        {
            w.WriteLine("-----------------------------------------------------------------");
            // #3561: the count is app groups, not lines — identical aborts are collapsed into one
            // line carrying its own ×N, so the header keeps saying how many app groups ran against
            // a partial company while the body stops repeating itself.
            w.WriteLine($"Company initialization: INCOMPLETE ({companyInitFailures.Sum(f => f.Count)} abort(s)) — "
                + "the tests above ran against a PARTIALLY initialized company.");
            foreach (var f in companyInitFailures)
                w.WriteLine($"  {DescribeCompanyInitFailure(f)}");
            if (companyInitFailures.All(f => f.AcceptedReason != null))
                w.WriteLine("  → accepted by tests/expectations (accept-partial-company-init), so the run "
                    + "still exits on what the tests earned. See docs/partial-company-initialization.md.");
            w.WriteLine("  → setup rows the codeunit had not reached are missing, so a failure "
                + "reading one is caused by this, not by the AL under test.");
        }
        // #4562: the seed belongs to the result — with a replay command when a test failed.
        // docs/run-seed.md documents this line; keep the value on stdout.
        if (options.Seed is int seed)
        {
            var firstFailing = buckets.Where(b => b.Stage == BucketStage.Ran)
                .SelectMany(b => b.Tests)
                .FirstOrDefault(t => t.Outcome is TestOutcome.Fail or TestOutcome.Error);
            w.WriteLine(firstFailing == null
                ? $"Seed:  {seed}"
                : $"Seed:  {seed}   replay one failure: al-runner --seed {seed} --test "
                    + $"{firstFailing.Codeunit}.{firstFailing.Method} {options.ReplayTarget ?? "<app>"}");
        }
    }

    /// <summary>
    /// Everything the package cache could not serve, ONCE per app, as the last block before the
    /// Result line (#4560). Nothing is printed at discovery (except under --verbose), so this is
    /// the only place a default run names a gap. The entries are the discovery messages
    /// verbatim — their wording is DependencyResolver's / ProvisionGapLog's, not this method's.
    /// </summary>
    public static void PrintActionNeeded(IReadOnlyList<BucketResult> buckets, TextWriter w)
    {
        var entries = ActionNeededEntries(buckets);
        if (entries.Count == 0) return;
        w.WriteLine();
        w.WriteLine($"Action needed ({entries.Count}):");
        foreach (var e in entries)
            foreach (var line in e.Replace("\r\n", "\n").Split('\n'))
                w.WriteLine("  " + line);
    }

    /// <summary>
    /// The gaps across all buckets, one per app. Keyed on the message's FIRST line, which names
    /// the app and what is wrong with it (`[dep] Publisher/Name vX resolved to ... NO
    /// IMPLEMENTATION`); the lines under it can differ per dependency edge (the winner path) and
    /// are not part of the key. The whole first line, not just the app: a floor gap names the
    /// dependent AND the floor, and two different floors are two things to fix.
    /// </summary>
    internal static IReadOnlyList<string> ActionNeededEntries(IReadOnlyList<BucketResult> buckets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var g in buckets.SelectMany(b => b.ProvisionGaps ?? Array.Empty<string>()))
        {
            var firstLine = g.Split('\n')[0].TrimEnd('\r');
            if (seen.Add(firstLine)) result.Add(g);
        }
        return result;
    }

    /// <summary>
    /// The run's last line (#4562): the verdict and what the exit code means, in the same
    /// words as `--help`'s exit-code table.
    /// </summary>
    public static string ResultLine(int exitCode, int? forcedFrom = null)
    {
        if (forcedFrom is int real && real != exitCode)
            return $"Result: {(real == 0 ? "PASSED" : "FAILED")}, exit code {exitCode} "
                + $"(--no-strict-exit; the run's own code is {real}: {ExitCodeMeaning(real)})";
        return $"Result: {(exitCode == 0 ? "PASSED" : "FAILED")}, exit code {exitCode} ({ExitCodeMeaning(exitCode)})";
    }

    internal static string ExitCodeMeaning(int exitCode) => exitCode switch
    {
        0 => "all tests passed",
        1 => "at least one test failed or errored",
        2 => "the run is not clean: an app could not execute, an output file could not be written, "
             + "or company initialization did not complete — see above",
        3 => "an app could not compile",
        4 => "--count-baseline: a suite's test count did not match its baseline",
        5 => "an expectations entry matched no test",
        6 => "--test selected no test",
        _ => "see al-runner --help",
    };

    // Per-test output. FAIL and ERROR entries always; PASS lines only when `showPass`
    // (--show-pass or --verbose, #4563). PASS keeps V1's `PASS  Codeunit.Method (Nms)` shape,
    // which bc-tests.yml greps as `^PASS +<Codeunit.Method>( |$)`.
    public static void PrintPerTest(IReadOnlyList<BucketResult> buckets, TextWriter w, bool showPass)
    {
        // #4562: one app's `=== <app> ===` header only repeats the summary; kept when PASS lines
        // are listed, because tools/preflight.py counts PASS lines per header.
        var headers = buckets.Count > 1 || showPass;
        foreach (var b in buckets)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                w.WriteLine();
                w.WriteLine($"=== {BundleLabel(b.BucketPath)} — COMPILE FAIL ===");
                foreach (var e in b.CompileErrors.Take(20)) w.WriteLine($"  {e}");
                if (b.CompileErrors.Count > 20)
                    w.WriteLine($"  ... and {b.CompileErrors.Count - 20} more compile errors");
                continue;
            }
            if (b.Stage == BucketStage.ExecuteFailed)
            {
                w.WriteLine();
                w.WriteLine($"=== {BundleLabel(b.BucketPath)} — EXEC FAIL ===");
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
                w.WriteLine($"=== {BundleLabel(b.BucketPath)} — SUITE ERRORS ({b.CompileErrors.Count}) ===");
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
            if (headers) w.WriteLine($"=== {BundleLabel(b.BucketPath)} ===");
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
                w.WriteLine(t.Outcome is TestOutcome.Fail or TestOutcome.Error
                    ? $"{label} {FailureHeading(t, ms)}{suspectSuffix}"
                    : $"{label} {t.Codeunit}.{t.Method} ({ms}ms){suspectSuffix}");
                if (t.Outcome != TestOutcome.Pass)
                {
                    if (!string.IsNullOrEmpty(t.Message))
                        w.WriteLine($"      {ConsoleMessage(t.Message)}");
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

    /// <summary>
    /// The label a bundle is printed under: its directory name, with a trailing separator
    /// removed first — <c>Path.GetFileName("appB/")</c> is empty, which printed <c>===  ===</c>
    /// (#4562). The full path stays in --output-json and --out.
    /// </summary>
    internal static string BundleLabel(string bucketPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(bucketPath);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    /// A failure entry's heading (#4566): the codeunit's AL name first, and its CodeunitN id in
    /// the parenthesis, because --test / --exclude-test take the id. tools/ci-wait.py reads the
    /// id out of this line — change both together.
    /// </summary>
    internal static string FailureHeading(TestResult t, long ms)
    {
        var name = t.CodeunitDisplayName;
        return string.IsNullOrEmpty(name) || name == t.Codeunit
            ? $"{t.Codeunit}.{t.Method} ({ms} ms)"
            : $"\"{name}\".{t.Method} ({t.Codeunit}, {ms} ms)";
    }

    private const string DialogExceptionPrefix = "NavNCLDialogException: ";

    /// <summary>
    /// The failure message as the console shows it (#4566). <c>NavNCLDialogException</c> is BC's
    /// type for an AL <c>Error()</c> or a failed assertion, so its name is dropped; every other
    /// type name stays, because it points at a runner or provisioning problem. --output-json,
    /// --out and JUnit carry <see cref="TestResult.Message"/> unchanged.
    /// </summary>
    internal static string ConsoleMessage(string message)
        => message.StartsWith(DialogExceptionPrefix, StringComparison.Ordinal)
            ? message[DialogExceptionPrefix.Length..]
            : message;

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

        var companyInitFailures = CompanyInitFailures(buckets);

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
            // #2502: pass it back as --seed to reproduce this run's Random() values.
            seed = Infrastructure.RunSeed.Resolved,
            compilationErrors = compileErrors.Count > 0 ? compileErrors : null,
            executionErrors = executionErrors.Count > 0 ? executionErrors : null,
            suiteErrors = suiteErrors.Count > 0 ? suiteErrors : null,
            // #3538: the condition a consumer could not previously see at all. It is not a test
            // result and not a bucket stage, so it appeared in none of the arrays above while
            // every test in the document had run against a half-initialized company. Additive
            // and null-omitted, so a run whose company initialized cleanly serialises
            // byte-identically to before.
            companyInitFailures = companyInitFailures.Count > 0
                ? companyInitFailures.Select(f => new
                {
                    codeunitId = f.CodeunitId,
                    codeunit = f.CodeunitName,
                    exceptionType = f.ExceptionType,
                    message = f.Message,
                    // #3561: how many app groups reported this same abort, and the manifest
                    // reason accepting it. `count` is always present so a consumer never has to
                    // decide whether a missing field means one or none; `accepted` is
                    // null-omitted, so a document from a run with no acceptance is unchanged.
                    count = f.Count,
                    accepted = f.AcceptedReason,
                }).ToList()
                : null,
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
                // #3538: same gap as #2762 one condition over. This file is the triage
                // worklist, and every failing record under a bucket whose company did not
                // finish initializing may be that condition rather than a runner gap worth
                // chasing. Its own kind, ranked first so it is read before the records it may
                // explain.
                foreach (var f in b.CompanyInitFailures ?? Array.Empty<CompanyInitFailure>())
                {
                    failures.Add(new
                    {
                        bucket = b.BucketPath,
                        kind = "company-init",
                        codeunitId = f.CodeunitId,
                        codeunit = f.CodeunitName,
                        exceptionType = f.ExceptionType,
                        message = f.Message,
                        // #3561 — see the JSON document's own fields.
                        count = f.Count,
                        accepted = f.AcceptedReason,
                        classification = "company-init/partial",
                    });
                }
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

        // A BC shape gap gets its own bucket (#2946). Without it the classifier below would
        // key it on whatever incidental BC frame happens to be innermost on the stack, which
        // names the code the runner was SERVING rather than the read that failed — and every
        // shape gap in a run would scatter across unrelated runtime/* buckets.
        int gapIdx = message.IndexOf(Infrastructure.BcShapeGapException.Prefix, StringComparison.Ordinal);
        var gapText = gapIdx >= 0 ? message : full;
        if (gapIdx < 0) gapIdx = full.IndexOf(Infrastructure.BcShapeGapException.Prefix, StringComparison.Ordinal);
        if (gapIdx >= 0)
        {
            var tail = gapText[(gapIdx + Infrastructure.BcShapeGapException.Prefix.Length)..];
            int nl = tail.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) tail = tail[..nl];
            int sep = tail.IndexOf(" — ", StringComparison.Ordinal);
            var surface = (sep >= 0 ? tail[..sep] : tail).Trim();
            return surface.Length == 0 ? "bc-shape-gap/unknown" : $"bc-shape-gap/{surface}";
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
