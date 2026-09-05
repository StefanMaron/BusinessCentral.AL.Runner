// CarriedAttemptFiles — is every attempt this run was PROMISED actually here? (issue #2747)
//
// A watchdog resume (#2280) hands the next process one file per earlier attempt: a JUnit for the
// summary and --output-junit (--merge-counts), and since #2719 a JSON sidecar for --output-json,
// --out and --count-baseline (--merge-results). Both readers were written to shrug at a file
// they cannot use — JUnitReport.LoadCarriedSuites skips it, JUnitCounts.Read returns zero
// totals — and for a CORRUPT file that is the right instinct: the run already happened, and one
// unreadable scratch file is not a verdict on it.
//
// It is the wrong instinct for a file that is simply GONE, because of how it goes gone. The
// carry directory is owned by the attempt that wrote it (ScratchDirs, #2706), and that attempt
// is the PARENT, which then sits waiting while the child runs. The child does not read the
// carry files until it writes its own outputs, at the very end — so the window is the child's
// entire run, not an instant. Two ways the parent's death takes the file with it:
//
//   * SIGTERM to the parent alone — its ProcessExit handler runs and deletes the directory it
//     owns, while the child is still running;
//   * SIGKILL to the parent alone — nothing runs, but the owner is now dead, so the next runner
//     start anywhere on the machine sweeps the directory correctly and legitimately.
//
// On a busy machine under --jobs the second is the more likely one. In both cases the child
// finishes, finds the file absent, silently omits every earlier attempt, and reports a SMALLER
// run as a clean one. That is the failure mode this repository treats as the worst kind: a
// result that shrank without saying so, wearing exit code 0.
//
// So this class draws the line the readers could not: a file NAMED on the command line and not
// usable is a promised input that vanished, and the run may not report as if it never existed.
// A missing carry file is not a reason to discard the run's own results — the summary and the
// exit code still describe what this process ran — it is a reason to refuse to present the
// report as complete.

using System.Xml.Linq;

namespace AlRunner.Infrastructure;

public static class CarriedAttemptFiles
{
    /// <summary>Why one named carry file could not contribute.</summary>
    public sealed record Unusable(string Path, string Reason);

    /// <summary>
    /// Every file named by <c>--merge-counts</c> / <c>--merge-results</c> that cannot contribute.
    /// Empty for a run that was handed none, which is every run that never resumed.
    /// </summary>
    public static List<Unusable> Audit(
        IEnumerable<string> junitFiles, IEnumerable<string> resultFiles)
    {
        var bad = new List<Unusable>();
        foreach (var f in junitFiles) { var r = JUnitProblem(f); if (r != null) bad.Add(new Unusable(f, r)); }
        foreach (var f in resultFiles) { var r = ResultsProblem(f); if (r != null) bad.Add(new Unusable(f, r)); }
        return bad;
    }

    private static string? JUnitProblem(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;   // never named; nothing was promised
        if (!File.Exists(path))
            return "the file is gone — most likely deleted with the scratch directory of the "
                 + "attempt that wrote it (see this file's header)";
        try
        {
            var root = XDocument.Load(path).Root;
            if (root == null) return "the file is empty";
            var suites = root.Name.LocalName == "testsuite"
                ? 1
                : root.Elements("testsuite").Count();
            // Zero suites is what an attempt killed mid-write leaves behind, and it is
            // indistinguishable from an attempt that genuinely ran nothing — which cannot
            // happen, because an attempt only writes a carry file when it HAS results.
            return suites > 0 ? null : "the file holds no testsuite elements (truncated?)";
        }
        catch (Exception ex) { return $"the file will not parse as JUnit ({ex.GetType().Name})"; }
    }

    private static string? ResultsProblem(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path))
            return "the file is gone — most likely deleted with the scratch directory of the "
                 + "attempt that wrote it (see this file's header)";
        try
        {
            var back = ResumeCarry.Read(new[] { path }, out var unreadable);
            if (unreadable > 0) return "the file will not parse as carried results";
            return back.Count > 0 ? null : "the file holds no buckets (truncated?)";
        }
        catch (Exception ex) { return $"the file will not parse as carried results ({ex.GetType().Name})"; }
    }

    /// <summary>The message a run prints before refusing to report as complete. One line per
    /// file, because which attempt was lost is the first thing anyone will ask.</summary>
    public static string Describe(IReadOnlyList<Unusable> bad)
    {
        var lines = new List<string>
        {
            $"resume: {bad.Count} carried attempt file(s) named on the command line could not be "
            + "used, so this run's report would silently omit whole attempts. Refusing to report "
            + "it as complete (issue #2747):",
        };
        foreach (var b in bad) lines.Add($"  {b.Path}: {b.Reason}");
        lines.Add(
            "The results THIS process produced are still printed above and are unaffected; what "
            + "cannot be trusted is the total, so treat this run as incomplete and re-run it.");
        return string.Join(Environment.NewLine, lines);
    }
}
