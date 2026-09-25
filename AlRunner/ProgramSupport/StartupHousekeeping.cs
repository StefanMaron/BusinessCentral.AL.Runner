namespace AlRunner;

// Once-per-invocation startup housekeeping: the stale-scratch sweep (#2706) and the
// package-dedup prune (#2990). A re-exec parent runs it and hands the fact to its IMMEDIATE
// child, which then skips it; see docs/startup-cost.md#startup-housekeeping (#2375).
internal static partial class ProgramSupport
{
    // Deliberately NOT AL_RUNNER_NCL_SHADOW_DONE: that one is also set by hand to run a shadow
    // dir directly, and such a run has no parent that swept for it.
    internal const string StartupHousekeepingHandOffEnvVar = "AL_RUNNER_STARTUP_HOUSEKEEPING_DONE";

    // Marks a re-exec child's environment: its parent has run the housekeeping moments ago.
    internal static void HandOffStartupHousekeeping(System.Diagnostics.ProcessStartInfo psi) =>
        psi.Environment[StartupHousekeepingHandOffEnvVar] = "1";

    internal static bool ConsumeStartupHousekeepingHandOff() =>
        ConsumeStartupHousekeepingHandOff(Environment.GetEnvironmentVariable,
            name => Environment.SetEnvironmentVariable(name, null));

    // Reads AND clears the hand-off, so it covers exactly one generation: a process this one
    // spawns later (a --jobs worker, an abort-resume child) must sweep again, because the
    // processes that leave stale scratch behind are the ones that die during this invocation.
    internal static bool ConsumeStartupHousekeepingHandOff(Func<string, string?> read, Action<string> clear)
    {
        var handedOff = read(StartupHousekeepingHandOffEnvVar) == "1";
        clear(StartupHousekeepingHandOffEnvVar);
        return handedOff;
    }

    internal static void RunStartupHousekeeping()
    {
        // #2706: reclaim the scratch directories of runner / test-host processes that no longer
        // exist (killed, OOM'd, watchdog-aborted — they cannot clean up after themselves). Runs
        // before argument parsing so it does not depend on which mode this invocation is in, and
        // before any BC type loads. Only directories whose recorded owner is provably dead are
        // removed; see ScratchDirs for the rules.
        try
        {
            var swept = AlRunner.Infrastructure.ScratchDirs.SweepStale();
            // Deleting other processes' directories is the only destructive thing the runner does
            // on its own initiative, so say so UNCONDITIONALLY — not behind AL_RUNNER_PERF. If the
            // liveness test ever misjudges a live owner, this line is the only evidence.
            if (swept.Removed.Count > 0)
                Console.Error.WriteLine($"scratch sweep: reclaimed {swept.Removed.Count} stale dir(s) from dead runners under {Path.GetTempPath()}");
            if (swept.Removed.Count > 0 || swept.Failed > 0)
                PerfTrace.Log($"scratch sweep: removed {swept.Removed.Count} stale dir(s), kept {swept.Kept}, failed {swept.Failed} under {Path.GetTempPath()}");
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"scratch sweep skipped: {ex.GetType().Name}: {ex.Message}");
        }

        // #2990: the package-dedup staging root is deliberately SHARED and owner-less — a stage
        // must outlive the run that created it — so the owner-liveness rule above cannot reclaim
        // anything there. PkgDedupCache removes only what it can prove is both unclaimed by a
        // live process and unused for a week; see its header.
        try
        {
            var pruned = AlRunner.Infrastructure.PkgDedupCache.Prune();
            // Announced unconditionally for the same reason as the sweep above.
            if (pruned.Removed.Count > 0)
                Console.Error.WriteLine($"pkgdedup prune: reclaimed {pruned.Removed.Count} unused staging dir(s) under {AlRunner.Infrastructure.PkgDedupCache.Root}");
            if (pruned.Removed.Count > 0 || pruned.Failed > 0 || pruned.MarkersRemoved > 0)
                PerfTrace.Log($"pkgdedup prune: removed {pruned.Removed.Count}, kept {pruned.Kept}, skipped {pruned.Skipped}, failed {pruned.Failed}, markers {pruned.MarkersRemoved}");
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"pkgdedup prune skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
