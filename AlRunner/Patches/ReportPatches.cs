// ReportPatches — static NavReport.Run / NavReport.RunModal replacements.
//
// REPORT.RUN(id [, reqPage [, sysPrinter [, record]]]) in AL compiles to static
// NavReport.Run(int, ...) overloads, and REPORT.RUNMODAL(...) to NavReport.RunModal(...).
// Without hooks these call NCLMetadata.GetMetaReportById → ThrowMetaApplicationObjectNotFound
// for every test-assembly report.
//
// Policy: in-process construction of a NavReport from an id is not yet wired (would need
// a sync analogue of NavReportHandle.CreateTarget driven by reportId). Until it lands,
// the static Run / RunModal overloads throw an AL-observable InvalidOperationException
// with the "out-of-scope:" prefix. Tests wrap these calls in `asserterror` +
// `Assert.ExpectedError('out-of-scope: static NavReport.Run')`. No silent no-ops.
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    private const string StaticRunOosPrefix =
        "out-of-scope: static NavReport.Run (in-process construction from reportId not yet wired; " +
        "construct the report as an AL variable and call instance Run() instead)";
    private const string StaticRunModalOosPrefix =
        "out-of-scope: static NavReport.RunModal (in-process construction from reportId not yet wired; " +
        "construct the report as an AL variable and call instance Run() instead)";

    // ──────────────────────────────────────────────────────────────────
    // NavReport.Run / RunModal instance (0-arg, void) — execute lifecycle
    // ──────────────────────────────────────────────────────────────────
    // The Cecil rewrite in NclCecilRewrite.cs leaves the instance Run() / RunModal()
    // bodies as a `ret` placeholder. At runtime, BcRuntime wires a JmpHook here so
    // the call instead dispatches to NavReportSync.SyncRun(this), which reflectively
    // invokes OnInitReport / OnPreReport / DataItem Pre+Post / OnPostReport on the
    // same NavReport instance the AL code constructed. Managed→managed call —
    // avoids a cross-assembly metadata reference inside the rewritten Ncl.dll.

    // NavReport.Run/RunModal — Cecil-rewritten directly to call SyncRun(this).
    // The static overloads below remain JmpHook targets (OOS throws).
    // Instance NavReport_InstanceRun{,Modal} kept here for any external caller
    // that wants to invoke the lifecycle programmatically.

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

    // ──────────────────────────────────────────────────────────────────
    // NavReport.Run static overloads — throw OOS (no silent no-ops)
    // ──────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRun1(int reportId)
    {
        throw new InvalidOperationException($"{StaticRunOosPrefix} [reportId={reportId}]");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRun2(int reportId, bool requestWindow)
    {
        throw new InvalidOperationException($"{StaticRunOosPrefix} [reportId={reportId}]");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRunOpts(object reportRunOptions)
    {
        throw new InvalidOperationException(StaticRunOosPrefix);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRun3(int reportId, bool requestWindow, bool systemPrinter)
    {
        throw new InvalidOperationException($"{StaticRunOosPrefix} [reportId={reportId}]");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRun4(int reportId, bool requestWindow, bool systemPrinter, object record)
    {
        throw new InvalidOperationException($"{StaticRunOosPrefix} [reportId={reportId}]");
    }

    // ──────────────────────────────────────────────────────────────────
    // NavReport.RunModal static overloads — throw OOS
    // ──────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRunModal1(int reportId)
    {
        throw new InvalidOperationException($"{StaticRunModalOosPrefix} [reportId={reportId}]");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRunModal2(int reportId, bool requestWindow)
    {
        throw new InvalidOperationException($"{StaticRunModalOosPrefix} [reportId={reportId}]");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRunModal3(int reportId, bool requestWindow, bool systemPrinter)
    {
        throw new InvalidOperationException($"{StaticRunModalOosPrefix} [reportId={reportId}]");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavReport_StaticRunModal4(int reportId, bool requestWindow, bool systemPrinter, object record)
    {
        throw new InvalidOperationException($"{StaticRunModalOosPrefix} [reportId={reportId}]");
    }
}
