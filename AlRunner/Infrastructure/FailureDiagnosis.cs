// FailureDiagnosis — the one place a failing test's optional explanation is composed.
//
// There is more than one thing worth saying next to a failure now, and they are independent:
// MissingTestDataDiagnosis (#2240) explains a failure against a table with no rows in it,
// MaskedTriggerErrorDiagnosis (#3189) names the AL error a TestPage teardown was reported in
// place of. Both can apply to one failure — a page trigger that failed on a missing setup
// record is exactly that case — so the composer joins them rather than picking one, and it
// exists so the four TestResult sites in TestExecutor cannot drift into asking different
// questions.
//
// Everything the two of them promise individually holds here: this only ever ADDS text beside a
// failure. It never replaces the failure's own message, never changes its outcome, and never
// reaches AL (.claude/rules/loud-failures.md).
//
// ONE LINE, still. The bundle reporter keeps only line 1 of a message (#2261), so the join is a
// space and neither half may contain a line break.
using System;

namespace AlRunner.Infrastructure;

internal static class FailureDiagnosis
{
    internal static string? Explain(Exception? ex)
    {
        // Masked cause first: it says WHICH error is being explained, so a missing-data
        // sentence after it reads as being about that error rather than about BC's stand-in
        // message.
        var masked = MaskedTriggerErrorDiagnosis.Explain(ex);
        var missingData = MissingTestDataDiagnosis.Explain(ex);

        if (masked == null) return missingData;
        if (missingData == null) return masked;
        return masked + " " + missingData;
    }
}
