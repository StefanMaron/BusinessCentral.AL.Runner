// AbortResume — re-run what a watchdog abort abandoned, in a fresh process (issue #2280).
//
// A hung codeunit used to take the whole run down: TestExecutor abandons the rest of that
// codeunit and every later codeunit in the bundle. That in-process behaviour is right and is not
// what changes — the hung thread is never killed (Thread.Join times out; nothing aborts it) and
// keeps mutating shared BC state, so continuing there would report results that lie.
//
// What changes is that the runner no longer stops. A fresh process has trustworthy state, so the
// bundle is re-run with the hung codeunit excluded. That attempt may hit a DIFFERENT hang and
// resume again, each time carrying the accumulated exclusions and one less of the budget.
//
// Measured on Tests-ERM (BC 28.1, --test-data): 2 tests ran before the first abort, 1,066 with
// the first hung codeunit excluded, 2,145 with the second.
//
// The attempt re-runs the bundle from the START, so its result REPLACES the previous attempt's
// rather than being merged into it. One consequence is stated rather than hidden: tests that ran
// inside the hung codeunit BEFORE it hung are not in the final total, because that codeunit is
// now excluded. The notice names what was dropped so the number is not mistaken for a complete
// one.

namespace AlRunner.Infrastructure;

internal static class AbortResume
{
    /// <summary>
    /// How many times a run may resume itself before giving up. Bounded because each attempt
    /// costs a full BC boot, and because a bundle that hangs in a dozen different codeunits is
    /// telling you something the retries will not fix.
    /// </summary>
    public const int DefaultBudget = 5;

    /// <summary>
    /// The child command line: this process's own arguments, with any previous
    /// <c>--exclude-test</c> / <c>--resume-aborts</c> pairs stripped and the accumulated ones
    /// appended. Stripping first matters — appending to arguments that already carry
    /// <c>--resume-aborts</c> would leave two, and the parser takes the last, so the budget
    /// would never count down and a genuinely stuck run would retry forever.
    /// </summary>
    public static List<string> BuildChildArgs(
        IReadOnlyList<string> originalArgs, IReadOnlyCollection<string> exclusions, int remainingBudget)
    {
        var child = new List<string>();
        for (var i = 0; i < originalArgs.Count; i++)
        {
            var a = originalArgs[i];
            if (a == "--exclude-test" || a == "--resume-aborts")
            {
                if (i + 1 < originalArgs.Count) i++;
                continue;
            }
            child.Add(a);
        }
        foreach (var e in exclusions) { child.Add("--exclude-test"); child.Add(e); }
        child.Add("--resume-aborts");
        child.Add(remainingBudget.ToString());
        return child;
    }

    /// <summary>
    /// Spawn the retry and return its exit code. Output is inherited rather than captured, so
    /// the retry's own progress and summary reach the user live — a run that silently went quiet
    /// for the length of another full attempt would be worse than the abort it is recovering
    /// from.
    /// </summary>
    public static int Rerun(IReadOnlyList<string> originalArgs, IReadOnlyCollection<string> exclusions, int remainingBudget)
    {
        var childArgs = BuildChildArgs(originalArgs, exclusions, remainingBudget);

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"resume: a watchdog abort ended this run early. Re-running in a fresh process with "
            + $"{exclusions.Count} codeunit(s) excluded ({string.Join(", ", exclusions)}); "
            + $"{remainingBudget} resume attempt(s) left after this one.");
        Console.Error.WriteLine(
            "resume: the retry's totals REPLACE the ones above. Tests inside an excluded codeunit "
            + "are not counted in them.");
        Console.Error.WriteLine();

        var exe = Environment.ProcessPath ?? "dotnet";
        var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var viaDotnet = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                     || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        var psi = new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = false };
        if (viaDotnet && asm != null) psi.ArgumentList.Add(asm);
        foreach (var a in childArgs) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null)
        {
            Console.Error.WriteLine("resume: could not start the retry process; reporting the aborted run as-is.");
            return 3;
        }
        p.WaitForExit();

        // A resumed run must NEVER report clean success. The retry can legitimately exit 0 — the
        // tests it ran all passed — but a codeunit was excluded because it hung, and its tests
        // did not run at all. Returning the child's 0 would turn a hang into a green run, which
        // is the failure mode this whole mechanism exists to make visible rather than hide.
        // SuiteAbortOnTimeoutTests.HungTest_AbortsCodeunit_ReportsCodeunitAndSkippedCount caught
        // exactly this while the resume was first being written.
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"resume: finished after excluding {exclusions.Count} hung codeunit(s): "
            + $"{string.Join(", ", exclusions)}. Their tests did NOT run, so this run is not clean "
            + "however the retry's own totals read.");
        return p.ExitCode != 0 ? p.ExitCode : 1;
    }
}
