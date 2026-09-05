// BundleFailureStage — which pipeline stage a bundle's suite-error lines describe.
//
// WHY THIS EXISTS (issue #2779)
//   A bundle that produced zero tests used to be reported as COMPILE FAIL unconditionally:
//
//       if (bundleTests.Count == 0 && bundleErrors.Count > 0) stage = BucketStage.CompileFailed;
//
//   The stage is not a cosmetic label. It decides the "=== <bundle> — COMPILE FAIL ===" header,
//   the `compile-fail:` / `exec-fail:` counters in the summary, and `"kind": "compile"` plus a
//   `compile/*` classification in --out's JSON. Measured on the ms-bucket workflow's first run
//   (Actions run 33967273260): the BC backup reader refused the backup, every AL object had
//   compiled cleanly, and the run reported `compile-fail: 1`, `exec-fail: 0` and
//   `"classification": "compile/other"`. The reader of that report goes looking for AL compile
//   errors that do not exist.
//
//   BucketStage.ExecuteFailed already existed and was reachable only from the out-of-process
//   (fan-out) path, never from the in-process run loop that produces these lines.
//
// HOW THE STAGE IS DECIDED
//   Every line Program.cs appends to `bundleErrors` starts with `<name>: <MARKER>`, and the
//   marker says which stage failed. Classification is by marker, and the marker set is CLOSED:
//   an unrecognised marker is treated as pre-execution, and BundleFailureStageTests scans
//   Program.cs's own `bundleErrors.Add` literals and fails if one appears that this file does
//   not know about — so adding a marker without classifying it is a build failure, not a
//   silently mislabelled report.
//
//   A bundle is ExecuteFailed only when EVERY error is an execution-stage failure. A mix means
//   something genuinely failed to compile, and hiding that behind "exec fail" would be the same
//   wrong-report defect pointing the other way.
namespace AlRunner.Infrastructure;

internal static class BundleFailureStage
{
    /// <summary>The marker on a line reporting a failure of the test RUN itself — the module
    /// compiled and loaded, and then something threw or was abandoned.</summary>
    internal const string ExecFail = "EXEC-FAIL";

    /// <summary>The marker on a line reporting codeunits abandoned by the per-test watchdog.
    /// Execution-stage for the same reason: the module ran.</summary>
    internal const string TestTimeoutAbort = "TEST-TIMEOUT-ABORT";

    /// <summary>Markers for a failure BEFORE anything could run — AL emit, BC's own AL
    /// diagnostics, or the C# compile of the emitted sources.</summary>
    internal static readonly IReadOnlyList<string> PreExecutionMarkers = new[]
    {
        "EMIT-TIMEOUT", "EMIT-FAIL", "EMIT-EXCLUDED", "EMIT-ZERO",
        "PARTIAL-EMIT-DROP", "AL-DIAGNOSTIC-FAIL", "COMPILE-FAIL",
    };

    /// <summary>Markers for a failure AFTER the module compiled and loaded.</summary>
    internal static readonly IReadOnlyList<string> ExecutionMarkers = new[] { ExecFail, TestTimeoutAbort };

    /// <summary>Every marker this file classifies. The drift guard compares Program.cs's
    /// literals against this set.</summary>
    internal static IEnumerable<string> KnownMarkers => PreExecutionMarkers.Concat(ExecutionMarkers);

    /// <summary>
    /// The marker of one bundle-error line, or null when the line carries none. The lines are
    /// `&lt;name&gt;: &lt;MARKER&gt;…`, where the name is an app-group assembly name, a suite
    /// name or the literal `&lt;bundled&gt;`, so the marker is the first upper-case token after
    /// the first ": " separator.
    /// </summary>
    internal static string? MarkerOf(string error)
    {
        if (string.IsNullOrEmpty(error)) return null;
        var sep = error.IndexOf(": ", StringComparison.Ordinal);
        if (sep < 0) return null;
        var rest = error.AsSpan(sep + 2);
        var end = 0;
        while (end < rest.Length && (char.IsAsciiLetterUpper(rest[end]) || rest[end] == '-')) end++;
        if (end == 0) return null;
        var token = rest[..end].ToString().TrimEnd('-');
        return token.Length == 0 ? null : token;
    }

    internal static bool IsExecutionFailure(string error)
    {
        var marker = MarkerOf(error);
        return marker != null && ExecutionMarkers.Contains(marker, StringComparer.Ordinal);
    }

    /// <summary>
    /// The stage to report for a bundle that produced no tests. ExecuteFailed only when every
    /// error is an execution-stage failure; anything else — including an unrecognised marker —
    /// stays CompileFailed, which is the conservative direction because that path prints the
    /// error list in full.
    /// </summary>
    internal static BucketStage Classify(IReadOnlyList<string> errors)
        => errors.Count > 0 && errors.All(IsExecutionFailure)
            ? BucketStage.ExecuteFailed
            : BucketStage.CompileFailed;
}
