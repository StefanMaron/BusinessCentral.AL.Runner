// MaskedTriggerErrorDiagnosis — issue #3189.
//
// THE PROBLEM, MEASURED
//   A page's row-load trigger (OnAfterGetRecord / OnAfterGetCurrRecord) that raises an AL error
//   tears the TestPage down, and what propagates to AL from then on is BC's own
//   "The TestPage is not open." — the raised error's own text never reaches the AL caller. That
//   is faithful (#2656, measured on 27.5 / 28.3 / 28.4) and it stays.
//
//   What was NOT faithful is that the runner then threw the raised error away. LiveNavTestPage
//   .Loaded stashed it in Exception.Data and nothing ever read it, so the error existed for the
//   length of one `throw` and became unreachable. In the Tests-SMB nightly of 2026-09-06 that
//   cost 132 failures whose entire reported text was
//
//       NavNCLDialogException: The TestPage is not open.
//
//   with no cause in the run log, the JSON classification or the JUnit report. All 132 turned
//   out to be one error — a CalcFields refusal raised inside a FactBox part's trigger (#3121) —
//   and finding that out took patching the runner to print what it had discarded and re-running
//   the bucket. Reading the message instead pointed at the TestPage close/open commits in the
//   same window, none of which was involved.
//
// WHAT THIS DOES, AND WHAT IT DELIBERATELY DOES NOT DO
//   Exactly what MissingTestDataDiagnosis (#2240) does, and for the same reason: it ADDS an
//   explanation next to a failure. It never replaces one, never downgrades an outcome, and
//   never touches the failing test's own message, exception type or AL call stack
//   (.claude/rules/loud-failures.md). Above all it does not make the converted error AL-visible:
//   asserterror and GetLastErrorText still see only BC's own message, which is what a real
//   service tier gives them. Fixture codeunit 70545's
//   MaskedPartTriggerError_AlStillSeesOnlyBcsOwnMessage is the arm that holds that line.
//
// NO GUESSING
//   The evidence is typed and the runner recorded it itself — the converted exception, carried
//   on the Data key below by the ONE site that builds the replacement. There is no text
//   matching here: "The TestPage is not open." is a BC resource string like any other, and
//   matching on it would be a guess that stops working the moment BC rewords or localises it.
//   No key, no explanation.
using System;

namespace AlRunner.Infrastructure;

internal static class MaskedTriggerErrorDiagnosis
{
    /// <summary>
    /// Exception.Data key carrying the AL error a page trigger raised, on the replacement
    /// exception the runner reports in its place. Written by
    /// LiveNavTestPage.MakeTestPageNotOpenException; read here and — so the mask does not hide
    /// evidence from it either — by MissingTestDataDiagnosis. A string key rather than an object
    /// one so it survives any dictionary copy.
    /// </summary>
    internal const string ConvertedErrorDataKey = "al-runner.testpage.converted-error";

    /// <summary>
    /// The AL error that <paramref name="ex"/> was reported in place of, or null when
    /// <paramref name="ex"/> is not a replacement. Walks the InnerException chain for the same
    /// reason MissingTestDataDiagnosis.TryNameTable does: by the time a failure reaches the
    /// reporter it may be wrapped by the reflection invoke path.
    /// </summary>
    internal static Exception? Unmask(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e.Data[ConvertedErrorDataKey] is Exception converted)
                return converted;
        return null;
    }

    /// <summary>
    /// The one-line explanation, or null when nothing was converted. One line because the bundle
    /// reporter keeps only line 1 of a message (#2261) — an explanation whose second line
    /// carried the actionable part would reach nobody — so the converted error's own text is
    /// flattened rather than reproduced with its line breaks.
    /// </summary>
    internal static string? Explain(Exception? ex)
    {
        var converted = Unmask(ex);
        if (converted == null) return null;

        return $"[testpage] this is BC's own message for a page whose row-load trigger raised; "
             + $"the error it raised, which BC does not show AL, was "
             + $"{converted.GetType().Name}: {OneLine(converted.Message)}";
    }

    /// <summary>Collapse every run of whitespace, including line breaks, to one space.</summary>
    private static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no message)";
        var sb = new System.Text.StringBuilder(text!.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
