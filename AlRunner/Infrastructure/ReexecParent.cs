using System.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>
/// Runs a re-exec child to completion from a parent that has nothing left to do but wait
/// for it (#4947). Such a parent allocates nothing while it waits, so no GC ever runs, and
/// everything it left behind — on a cold run the Cecil rewrite of Ncl.dll's module graph and
/// byte arrays — stays resident beside the child for the child's whole run.
/// </summary>
internal static class ReexecParent
{
    /// <summary>Starts <paramref name="psi"/>, returns this process's heap to the OS, waits, and returns the child's exit code.</summary>
    internal static int RunToExit(System.Diagnostics.ProcessStartInfo psi)
    {
        using var child = System.Diagnostics.Process.Start(psi)!;
        // After Start, not before: the collection then overlaps the child's own startup
        // instead of delaying it.
        ReleaseHeap();
        PhaseLog.SetWaitRss(Environment.WorkingSet);
        child.WaitForExit();
        return child.ExitCode;
    }

    /// <summary>
    /// One full, blocking, compacting collection in Aggressive mode, which also decommits the
    /// freed memory back to the OS; LOH included, where the rewrite's byte arrays live.
    /// Only for a process whose remaining work is a wait: it is a full stop-the-world GC.
    /// </summary>
    internal static void ReleaseHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
