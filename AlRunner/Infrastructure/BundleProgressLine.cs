// BundleProgressLine — the per-bundle progress line Program.cs prints while a run is going.
//
// WHY THIS EXISTS (issue #2746)
//   The line used to be a single Console.WriteLine in Program.cs's run loop:
//
//       → 225P/0F/0E across 225 tests, 1 suite errors (30.9s)
//
//   "1 suite errors" is a tally with no name attached. Program.cs collects the descriptions into
//   `bundleErrors` and hands them to BucketResult.CompileErrors, and #2762 made every END-OF-RUN
//   surface print them — the summary's "Suite errors" block, PrintPerTest's SUITE ERRORS header,
//   --output-json's `suiteErrors`, --out's `"kind": "suite"` record, the --watch tree. None of
//   those is this line. On tests/runner-extras a bundle takes minutes, and this line is what a
//   developer watching the log reads at the moment the loss happens; the explanation arrived
//   only at the end, thousands of lines later.
//
//   Extracted rather than inlined so the shape is unit-testable: the caller is 3,500 lines into
//   Program.cs behind a full BC run, which is why the line went four years without a test.
//
// THE CAP
//   #2880's bundle lost 23 suites at once. Printing 23 multi-line compile errors into the middle
//   of a running log buries the progress output it is attached to, so the list is capped — but
//   the cap SAYS how many it hid, because a silent truncation is a smaller instance of exactly
//   the defect this file is fixing. The full list is still printed unabridged by the summary and
//   by --output-json at the end of the run.
namespace AlRunner.Infrastructure;

internal static class BundleProgressLine
{
    /// <summary>How many suite errors are named inline under the progress line.</summary>
    internal const int MaxInlineErrors = 3;

    /// <summary>
    /// The progress line, plus one line per suite error when there are any. With no suite errors
    /// the result is exactly the single line Program.cs printed before — the negative every
    /// integration test asserting on this line depends on.
    /// </summary>
    internal static IEnumerable<string> Render(int pass, int fail, int error, int tests,
        IReadOnlyList<string> suiteErrors, TimeSpan elapsed)
    {
        yield return $"  → {pass}P/{fail}F/{error}E across {tests} tests, "
            + $"{suiteErrors.Count} suite errors ({elapsed.TotalSeconds:F1}s)";
        if (suiteErrors.Count == 0) yield break;

        // Verbatim, for the same reason Reporter repeats them verbatim: the message names the
        // stage marker (EMIT-EXCLUDED / COMPILE-FAIL / EXEC-FAIL), the module and the dropped
        // objects, and a paraphrase sends the reader back up the log (.claude/rules/loud-failures.md).
        foreach (var e in suiteErrors.Take(MaxInlineErrors))
            yield return $"      ! {e}";
        if (suiteErrors.Count > MaxInlineErrors)
            yield return $"      ! ... and {suiteErrors.Count - MaxInlineErrors} more "
                + "suite error(s) — full list in the run summary";
    }
}
