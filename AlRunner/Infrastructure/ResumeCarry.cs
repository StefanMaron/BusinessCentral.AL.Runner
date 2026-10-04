// ResumeCarry — the results of one watchdog-resume attempt, carried to the next process (#2719).
//
// A resumed run (#2280) re-execs and the final attempt runs only the codeunits no earlier
// attempt reached, so its `results` list is a SLICE of the run. Two outputs already reassemble
// the whole: the printed summary (totals, via --merge-counts) and --output-junit (cases copied
// verbatim, #2716). Three did not, and reported the slice as if it were the run:
//
//   --output-json      the tests[] array, its counters, and its own exitCode field
//   --out PATH         the failure-classification file
//   --count-baseline   the count compared against the baseline
//
// Why this file exists rather than reconstructing from the JUnit the other two already carry.
// A JUnit <testcase> has a name and a status. It does not have Expectation, and Expectation is
// what decides whether a failure is a real `fail` or a `pass-known-gap` / `pass-oos` /
// `pass-divergence` — exactly what --out and the expectations manifest exist to compute. A case
// reconstructed from XML would arrive with a null Expectation and be classified as an
// UNEXPECTED failure, so the classification file would go from silently missing the carried
// error to confidently misclassifying it. Trading a silent omission for a wrong answer is not a
// fix, so the attempt writes what it actually had.
//
// What deliberately does NOT cross the process boundary:
//
//   * TestResult.Exception — a live object. FullException already carries its text, which is
//     everything a later process can use; serialising an exception graph to resurrect it in
//     another process would be a fiction, not fidelity.
//   * TestResult.CapturedValues — only ever populated by the server's `execute` with
//     --capture-values (#1640), which is not a batch run and never resumes.
//
// Being explicit about the crossing set is the point. Making TestResult itself serialisable
// would have quietly carried both of the above the first time someone added a field.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlRunner.Infrastructure;

public static class ResumeCarry
{
    /// <summary>One test's result, reduced to what survives a process boundary.</summary>
    public sealed record CarriedTest(
        string Codeunit, string Method, AlRunner.TestOutcome Outcome, string? Message,
        string? FullException, long DurationTicks, string? AlCallStack,
        string? CodeunitDisplayName, ExpectationResult? Expectation,
        bool InsideTestProc, bool TimedOut, string? Diagnosis,
        // #5129: --output-json's `generatedStubs`. Trailing and defaulted like CompanyInitFailures below.
        List<string>? GeneratedStubs = null);

    /// <summary>One bundle's result. Timings are carried so a resumed run's reported wall time
    /// is the run's, not the last attempt's.</summary>
    public sealed record CarriedBucket(
        string BucketPath, BucketStage Stage, List<string> CompileErrors, string? ProcessError,
        List<CarriedTest> Tests, long EmitTicks, long CompileTicks, long RunTicks,
        int RanGroupCount, List<string>? ProvisionGaps,
        // #3538. Trailing and defaulted, so a carry file written by an earlier runner build
        // deserialises unchanged and simply carries no abort.
        List<CompanyInitFailure>? CompanyInitFailures = null);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Write ONE attempt's results. The caller must pass only what this process ran — the same
    /// rule the JUnit carry file follows, and for the same reason: each carry file holds exactly
    /// one attempt, so a chain of resumes cannot fold an earlier attempt in more than once.
    /// </summary>
    public static void Write(string path, IReadOnlyList<BucketResult> results)
    {
        var payload = new List<CarriedBucket>(results.Count);
        foreach (var b in results)
        {
            var tests = new List<CarriedTest>(b.Tests.Count);
            foreach (var t in b.Tests)
                tests.Add(new CarriedTest(
                    t.Codeunit, t.Method, t.Outcome, t.Message, t.FullException,
                    t.Duration.Ticks, t.AlCallStack, t.CodeunitDisplayName, t.Expectation,
                    t.InsideTestProc, t.TimedOut, t.Diagnosis,
                    t.GeneratedStubs == null ? null : new List<string>(t.GeneratedStubs)));
            payload.Add(new CarriedBucket(
                b.BucketPath, b.Stage, new List<string>(b.CompileErrors), b.ProcessError, tests,
                b.EmitTime.Ticks, b.CompileTime.Ticks, b.RunTime.Ticks, b.RanGroupCount,
                b.ProvisionGaps == null ? null : new List<string>(b.ProvisionGaps),
                b.CompanyInitFailures == null ? null : new List<CompanyInitFailure>(b.CompanyInitFailures)));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>
    /// The <paramref name="rows"/> no carried attempt has reported for this bundle (#5268). A resumed
    /// attempt recompiles the bundle and finds the same EMIT-EXCLUDED drop an earlier attempt already
    /// reported as SKIPPED rows, and those rows arrive again through the carry, so adding them a second
    /// time counts each once per attempt. Keyed on bundle, codeunit and method: a same-named codeunit in
    /// another bundle is not this one's row.
    /// </summary>
    public static List<TestResult> NotYetReported(
        IReadOnlyList<BucketResult> carried, string bucketPath, IReadOnlyList<TestResult> rows)
    {
        var reported = new HashSet<(string, string)>();
        foreach (var b in carried)
            if (string.Equals(b.BucketPath, bucketPath, StringComparison.Ordinal))
                foreach (var t in b.Tests) reported.Add((t.Codeunit, t.Method));
        return rows.Where(r => !reported.Contains((r.Codeunit, r.Method))).ToList();
    }

    /// <summary>
    /// One bucket per bundle for a resumed run's outputs (#5273). Every attempt compiles each bundle again
    /// and reports it, so the folded attempts hold one bucket per bundle each; --output-json, --out and
    /// --count-out treat every bucket as a bundle and read it once per attempt. Buckets of one bundle and
    /// one stage merge, and only those: a bundle whose attempts disagree about its stage stays one bucket
    /// per stage, never read as the other (a non-Ran bucket's tests are not reported). What a merge keeps is
    /// docs/watchdog-resume-reporting.md: the DISTINCT things, never fewer than the most any attempt saw.
    /// <paramref name="attempts"/> are in attempt order, the carried ones first and this process's last.
    /// </summary>
    public static List<BucketResult> MergeAttempts(IReadOnlyList<IReadOnlyList<BucketResult>> attempts)
    {
        var slots = new List<List<BucketResult>>();
        var slotOf = new Dictionary<(string, BucketStage, int), int>();
        foreach (var attempt in attempts)
        {
            // The same bundle twice in ONE attempt (two arguments naming it) stays two buckets: the n-th
            // of a bundle merges with the n-th of the others.
            var seen = new Dictionary<(string, BucketStage), int>();
            foreach (var b in attempt)
            {
                var bundle = (b.BucketPath, b.Stage);
                seen.TryGetValue(bundle, out var nth);
                seen[bundle] = nth + 1;
                var slot = (bundle.BucketPath, bundle.Stage, nth);
                if (!slotOf.TryGetValue(slot, out var at))
                {
                    at = slots.Count;
                    slotOf[slot] = at;
                    slots.Add(new List<BucketResult>());
                }
                slots[at].Add(b);
            }
        }
        return slots.Select(MergeOne).ToList();
    }

    private static BucketResult MergeOne(List<BucketResult> parts)
    {
        if (parts.Count == 1) return parts[0];
        var first = parts[0];
        var processErrors = parts.Select(p => p.ProcessError).Where(e => e != null).Distinct().ToList();
        return first with
        {
            CompileErrors = MostReported(parts.Select(p => p.CompileErrors)),
            ProcessError = processErrors.Count == 0 ? null : string.Join("; ", processErrors),
            Tests = parts.SelectMany(p => p.Tests).ToList(),
            EmitTime = TimeSpan.FromTicks(parts.Sum(p => p.EmitTime.Ticks)),
            CompileTime = TimeSpan.FromTicks(parts.Sum(p => p.CompileTime.Ticks)),
            RunTime = TimeSpan.FromTicks(parts.Sum(p => p.RunTime.Ticks)),
            // Every attempt enters every app group of the bundle, so the most any attempt entered is the
            // number of groups it has; a sum would count each group once per attempt.
            RanGroupCount = parts.Max(p => p.RanGroupCount),
            ProvisionGaps = parts.All(p => p.ProvisionGaps == null) ? null
                : parts.SelectMany(p => p.ProvisionGaps ?? Array.Empty<string>()).Distinct().ToList(),
            CompanyInitFailures = MergeCompanyInit(parts),
        };
    }

    /// <summary>Each error as many times as the attempt that reported it most did: one attempt's own repeats
    /// stay, the same line in a second attempt is the same suite error, and a line only one attempt has is kept.
    /// In order of first appearance.</summary>
    private static List<string> MostReported(IEnumerable<IReadOnlyList<string>> perAttempt)
    {
        var most = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var errors in perAttempt)
        {
            var here = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in errors)
            {
                here[e] = here.GetValueOrDefault(e) + 1;
                if (!most.ContainsKey(e)) { most[e] = 0; order.Add(e); }
            }
            foreach (var (e, n) in here) if (n > most[e]) most[e] = n;
        }
        return order.SelectMany(e => Enumerable.Repeat(e, most[e])).ToList();
    }

    /// <summary>An abort each attempt reports for the same app groups is one abort: its count is the most any
    /// attempt gave it. An attempt's own aborts are already collapsed by key.</summary>
    private static List<CompanyInitFailure>? MergeCompanyInit(List<BucketResult> parts)
    {
        if (parts.All(p => p.CompanyInitFailures == null)) return null;
        var merged = new List<CompanyInitFailure>();
        var at = new Dictionary<(int, string, string, string), int>();
        foreach (var f in parts.SelectMany(p => p.CompanyInitFailures ?? Array.Empty<CompanyInitFailure>()))
        {
            var key = (f.CodeunitId, f.CodeunitName, f.ExceptionType, f.Message);
            if (at.TryGetValue(key, out var i))
                merged[i] = merged[i] with
                {
                    Count = Math.Max(merged[i].Count, f.Count),
                    AcceptedReason = merged[i].AcceptedReason ?? f.AcceptedReason,
                };
            else { at[key] = merged.Count; merged.Add(f); }
        }
        return merged;
    }

    /// <summary>
    /// Read every carry file back into BucketResults, in the order given. A file that is missing
    /// or will not parse contributes NOTHING rather than failing the run — the same stance
    /// JUnitReport takes for a carried JUnit, and the reason is the same: the run has already
    /// happened, and refusing to report it because a scratch file went missing helps nobody.
    /// It is the quiet direction, which is why the caller says so on stderr (see Program.cs).
    /// </summary>
    public static List<BucketResult> Read(IEnumerable<string> paths, out int unreadable)
        => ReadAttempts(paths, out unreadable).SelectMany(a => a).ToList();

    /// <summary><see cref="Read"/>, one list per readable file: each file is one attempt, and
    /// <see cref="MergeAttempts"/> needs to know which buckets belong to the same one.</summary>
    public static List<List<BucketResult>> ReadAttempts(IEnumerable<string> paths, out int unreadable)
    {
        unreadable = 0;
        var attempts = new List<List<BucketResult>>();
        foreach (var p in paths)
        {
            List<CarriedBucket>? payload;
            try
            {
                if (!File.Exists(p)) { unreadable++; continue; }
                payload = JsonSerializer.Deserialize<List<CarriedBucket>>(File.ReadAllText(p), Options);
            }
            catch (Exception) { unreadable++; continue; }
            if (payload == null) { unreadable++; continue; }

            var all = new List<BucketResult>();
            attempts.Add(all);
            foreach (var b in payload)
            {
                var tests = new List<TestResult>(b.Tests.Count);
                foreach (var t in b.Tests)
                    tests.Add(new TestResult(
                        t.Codeunit, t.Method, t.Outcome, t.Message, t.FullException,
                        TimeSpan.FromTicks(t.DurationTicks), t.AlCallStack, t.CodeunitDisplayName,
                        Exception: null, Expectation: t.Expectation, InsideTestProc: t.InsideTestProc,
                        TimedOut: t.TimedOut, CapturedValues: null, Diagnosis: t.Diagnosis,
                        GeneratedStubs: t.GeneratedStubs));
                all.Add(new BucketResult(
                    b.BucketPath, b.Stage, b.CompileErrors, b.ProcessError, tests,
                    TimeSpan.FromTicks(b.EmitTicks), TimeSpan.FromTicks(b.CompileTicks),
                    TimeSpan.FromTicks(b.RunTicks), b.RanGroupCount, b.ProvisionGaps,
                    // #3538: an abort recorded by an earlier attempt is carried forward with
                    // that attempt's results. Dropping it here would be the same silent loss
                    // this field exists to stop, one surface over — the resumed run's document
                    // would describe tests that ran against a partial company and say nothing.
                    b.CompanyInitFailures));
            }
        }
        return attempts;
    }
}
