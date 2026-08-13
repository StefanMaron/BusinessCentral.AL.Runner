// ReportPatches — NavReport.Run / NavReport.RunModal instance helpers.
//
// The static NavReport.Run(int, ...) / RunModal(int, ...) overloads (AL
// `Report.Run(id, ...)` / `Report.RunModal(id, ...)`) used to be JmpHook targets defined
// here, each throwing an out-of-scope InvalidOperationException on the theory that
// in-process construction of a NavReport from a bare id was not yet wired. #1771: that
// JmpHook never actually fired — JmpHook.Apply silently skips any target that is not
// Cecil-owned under the default Cecil-only runtime (AL_RUNNER_ENABLE_JMPHOOK unset) — so the
// static call fell straight through the Cecil-rewritten `ret` placeholder body and silently
// did nothing (a false PASS, not the intended loud throw). Construction from a bare id was
// also, by then, already wired (NavReportSync.CreateReportInstance, used by
// NavReportHandle_CreateTarget and SyncRunRequestPage). Both overload families are now
// Cecil-owned directly: real execution for the `int[, bool[, bool[, NavRecord]]]` shapes via
// NavReportSync.SyncStaticRun, and a loud OOS throw (emitted as IL, not a JmpHook) for the
// unrecognised ReportRunOptions overload shape. See NclCecilRewrite.cs §NavReport block.
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    // ──────────────────────────────────────────────────────────────────
    // NavReport.Run / RunModal instance (0-arg, void) — execute lifecycle
    // ──────────────────────────────────────────────────────────────────
    // The Cecil rewrite in NclCecilRewrite.cs rewrites the instance Run() / RunModal()
    // bodies to call NavReportSync.SyncRun(this) directly. NavReport_InstanceRun{,Modal}
    // are kept here for any external caller that wants to invoke the lifecycle
    // programmatically (managed→managed call — avoids a cross-assembly metadata
    // reference inside the rewritten Ncl.dll).

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_InstanceRun(object self)
    {
        NavReportSync.SyncRun(self);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_InstanceRunModal(object self)
    {
        NavReportSync.SyncRun(self);
    }
}
