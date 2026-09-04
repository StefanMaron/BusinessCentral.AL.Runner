// AbortResumePlan — turn a watchdog abort into the next attempt (issue #2280).
//
// One hung codeunit should not take down the whole run, and today it does: TestExecutor
// abandons the rest of that codeunit AND every later codeunit in the bundle. That in-process
// behaviour is correct and is not what changes here — the hung thread is never killed
// (Thread.Join times out; nothing aborts it) and goes on mutating shared BC state, so continuing
// in the same process would report results that lie.
//
// What changes is that the runner no longer STOPS there. A fresh process has trustworthy state,
// so the remainder is re-run with the hung codeunit excluded, repeating while each attempt finds
// a NEW hang. Measured on Tests-ERM (BC 28.1, --test-data): 2 tests before, 1,066 after
// excluding the first hung codeunit, 2,145 after the second.
//
// Two decisions worth stating, because both have a wrong answer that looks reasonable:
//
//   * Exclude the CODEUNIT, not the single method. The same codeunit almost always hangs again
//     on its next method, so per-method exclusion costs a whole process (and its BC boot) per
//     method to reach the same place.
//   * Termination is decided by whether an attempt yields a NEW exclusion. An abort naming a
//     codeunit already excluded means the run is stuck — that is a genuine "no progress" signal,
//     and retrying would repeat forever.
//   * An abort that abandoned NOTHING outside the hung codeunit is not worth resuming. Resume
//     exists to recover the LATER codeunits a hang took down with it; when there are none, the
//     retry re-runs only what already ran, minus the hung codeunit — strictly less information
//     for the price of a whole BC boot, and it turns a loud abort into a bundle reporting
//     "0 tests, 0 suite errors", which is the exact signature the original abort bug had.
//     SuiteAbortOnTimeoutTests' fixture is that shape: one codeunit, and it is the one hanging.

using System.Text.RegularExpressions;
using AlRunner;

namespace AlRunner.Infrastructure;

internal static class AbortResumePlan
{
    // TestExecutor.RecordAbortedSuite's format: "<Display> (<TypeName>).<Method>: watchdog ...".
    // "… and <n> in <m> subsequent codeunit(s) did not run" — absent when the hang took nothing
    // else down with it, which is the case not worth resuming.
    private static readonly Regex LaterCodeunits =
        new(@"and\s+\d+\s+in\s+(?<m>\d+)\s+subsequent codeunit", RegexOptions.Compiled);

    /// <summary>True when this abort abandoned codeunits BEYOND the one that hung.</summary>
    public static bool AbandonedLaterCodeunits(string reason)
    {
        var m = LaterCodeunits.Match(reason ?? string.Empty);
        return m.Success && int.TryParse(m.Groups["m"].Value, out var n) && n > 0;
    }

    private static readonly Regex AbortLine =
        new(@"\((?<cu>[A-Za-z_][A-Za-z0-9_]*)\)\.(?<m>[A-Za-z_][A-Za-z0-9_]*): watchdog timeout aborted",
            RegexOptions.Compiled);

    /// <summary>
    /// Codeunits to exclude on the next attempt: everything already excluded, everything the
    /// aborts named, and — the point of this overload — every codeunit that already produced
    /// results. What is left is exactly the work no attempt has reached.
    ///
    /// Without the third set the retry re-runs the bundle FROM THE START, so a bundle that hangs
    /// late pays for its whole successful prefix again; under --jobs the unit of retry is the
    /// SHARD, so eight buckets re-run because one codeunit in one of them hung; and since the
    /// watchdog is wall-clock, the extra load makes further spurious aborts more likely, which
    /// triggers further re-runs.
    /// </summary>
    public static IReadOnlyList<string> NextExclusions(
        IEnumerable<string> abortReasons,
        IReadOnlyCollection<string> already,
        IReadOnlyCollection<TestResult> attempted)
    {
        var seed = new List<string>(already);
        var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
        foreach (var r in attempted)
            if (!string.IsNullOrEmpty(r.Codeunit) && seen.Add(r.Codeunit))
                seed.Add(r.Codeunit);
        return NextExclusions(abortReasons, seed);
    }

    /// <summary>Codeunits named by these abort reasons, plus everything already excluded.</summary>
    public static IReadOnlyList<string> NextExclusions(
        IEnumerable<string> abortReasons, IReadOnlyCollection<string> already)
    {
        var set = new List<string>(already);
        var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
        foreach (var r in abortReasons)
        {
            var m = AbortLine.Match(r ?? string.Empty);
            if (!m.Success) continue;
            var cu = m.Groups["cu"].Value;
            if (seen.Add(cu)) set.Add(cu);
        }
        return set;
    }

    /// <summary>
    /// True when another attempt is worth spawning: at least one abort names a codeunit not
    /// already excluded. False with no aborts (nothing to resume) and false when every abort
    /// names something already skipped (stuck — retrying would loop).
    /// </summary>
    public static bool MakesProgress(IEnumerable<string> abortReasons, IReadOnlyCollection<string> already)
    {
        var reasons = (abortReasons as IReadOnlyCollection<string> ?? abortReasons.ToList())
            .Where(AbandonedLaterCodeunits)
            .ToList();
        if (reasons.Count == 0) return false;
        return NextExclusions(reasons, already).Count > already.Count;
    }
}
