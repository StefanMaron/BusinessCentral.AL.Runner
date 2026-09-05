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
        bool InsideTestProc, bool TimedOut, string? Diagnosis);

    /// <summary>One bundle's result. Timings are carried so a resumed run's reported wall time
    /// is the run's, not the last attempt's.</summary>
    public sealed record CarriedBucket(
        string BucketPath, BucketStage Stage, List<string> CompileErrors, string? ProcessError,
        List<CarriedTest> Tests, long EmitTicks, long CompileTicks, long RunTicks,
        int RanGroupCount, List<string>? ProvisionGaps);

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
                    t.InsideTestProc, t.TimedOut, t.Diagnosis));
            payload.Add(new CarriedBucket(
                b.BucketPath, b.Stage, new List<string>(b.CompileErrors), b.ProcessError, tests,
                b.EmitTime.Ticks, b.CompileTime.Ticks, b.RunTime.Ticks, b.RanGroupCount,
                b.ProvisionGaps == null ? null : new List<string>(b.ProvisionGaps)));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>
    /// Read every carry file back into BucketResults, in the order given. A file that is missing
    /// or will not parse contributes NOTHING rather than failing the run — the same stance
    /// JUnitReport takes for a carried JUnit, and the reason is the same: the run has already
    /// happened, and refusing to report it because a scratch file went missing helps nobody.
    /// It is the quiet direction, which is why the caller says so on stderr (see Program.cs).
    /// </summary>
    public static List<BucketResult> Read(IEnumerable<string> paths, out int unreadable)
    {
        unreadable = 0;
        var all = new List<BucketResult>();
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

            foreach (var b in payload)
            {
                var tests = new List<TestResult>(b.Tests.Count);
                foreach (var t in b.Tests)
                    tests.Add(new TestResult(
                        t.Codeunit, t.Method, t.Outcome, t.Message, t.FullException,
                        TimeSpan.FromTicks(t.DurationTicks), t.AlCallStack, t.CodeunitDisplayName,
                        Exception: null, Expectation: t.Expectation, InsideTestProc: t.InsideTestProc,
                        TimedOut: t.TimedOut, CapturedValues: null, Diagnosis: t.Diagnosis));
                all.Add(new BucketResult(
                    b.BucketPath, b.Stage, b.CompileErrors, b.ProcessError, tests,
                    TimeSpan.FromTicks(b.EmitTicks), TimeSpan.FromTicks(b.CompileTicks),
                    TimeSpan.FromTicks(b.RunTicks), b.RanGroupCount, b.ProvisionGaps));
            }
        }
        return all;
    }
}
