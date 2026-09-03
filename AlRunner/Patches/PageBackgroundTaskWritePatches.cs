// PageBackgroundTaskWritePatches — BC's read-only-session write refusal for page background
// task worker codeunits, enforced at the AL write entry points.
//
// WHY THIS EXISTS
//   RunnerPageBackgroundTaskGap.cs runs a page background task's worker codeunit inline
//   against the CURRENT session (see that file's header for why), setting
//   NavSession.PageBackgroundTask for the duration of the worker's OnRun — exactly the same
//   field BC's own (unmodified) PageBackgroundChildSessionTask.RunTaskInChildSessionAsync
//   sets around the same call. Left at that, the worker's Insert()/Modify()/Delete()/Rename()
//   would land on the runner's in-memory store like any ordinary write — but on a real tier
//   they do not.
//
// WHAT REAL BC DOES (measured, not assumed)
//   Issue #2514, corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#135, BC 27.5 and
//   28.3: a worker codeunit's Insert() and Modify(), called directly (no TryFunction — an
//   earlier probe wrapping the write in [TryFunction] measured an unrelated restriction on
//   TryFunction being the first call from a freshly-dispatched root scope instead), both
//   throw BC's own permission-denied wording verbatim:
//     "Sorry, the current permissions prevented the action.
//      (TableData 60790 Test Page BgTask Row Insert: AL Language Coverage Tests)"
//   The row does not exist afterward (Insert) and is unchanged afterward (Modify) — the
//   write never lands, not even locally-then-rolled-back. Consistent with the decompiled
//   platform code: NavChildSessionTask.RunInReadOnlySession defaults true and
//   PageBackgroundChildSessionTask never overrides it, so the child session BC's own
//   NavChildSessionTaskRuntime<T>.RunAsync would have built runs read-only, and a read-only
//   session's TableData permission for every write operation is unconditionally denied.
//
// PRECOMPILED-DLL RESPECT
//   No AL business-logic body is touched. These are static helpers Cecil PREPENDS to
//   NavRecord's own AL write entry points in the runtime engine (Ncl.dll) — the same
//   mechanism AllProfileWritePatches.cs and the rowversion-clock prepend already use. A
//   no-op for every write EXCEPT one made from inside a page background task worker.
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner.Patches;

public static class PageBackgroundTaskWritePatches
{
    /// <summary>Prepended to every NavRecord.ALInsertAsync overload.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardPageBackgroundTaskInsert(object? record)
        => ThrowIfDenied(record, "Insert", NavInsertDeniedPermissionException.InsertDeniedErrorCode);

    /// <summary>Prepended to every NavRecord.ALModifyAsync overload.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardPageBackgroundTaskModify(object? record)
        => ThrowIfDenied(record, "Modify", NavModifyDeniedPermissionException.ModifyDeniedErrorCode);

    /// <summary>Prepended to every NavRecord.ALDeleteAsync overload.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardPageBackgroundTaskDelete(object? record)
        => ThrowIfDenied(record, "Delete", NavDeleteDeniedPermissionException.DeleteDeniedErrorCode);

    /// <summary>Prepended to every NavRecord.ALRenameAsync overload.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GuardPageBackgroundTaskRename(object? record)
        => ThrowIfDenied(record, "Rename", NavModifyDeniedPermissionException.ModifyDeniedErrorCode);

    private static void ThrowIfDenied(object? record, string operationName, int errorCode)
    {
        if (record is not NavRecord rec) return;
        if (rec.ParentSession?.PageBackgroundTask == null) return;

        var tableId = rec.MetaTable?.TableId ?? rec.ObjectId.ObjectNumber;
        var tableName = rec.MetaTable?.TableName ?? rec.ObjectName;
        var message = "Sorry, the current permissions prevented the action. " +
                      $"(TableData {tableId} {tableName} {operationName})";
#pragma warning disable CS0618 // the message-only ctor is obsolete; the specific-diagnostic-params
                               // ctor needs CultureInfo/DiagnosticParameter plumbing this narrow,
                               // always-the-same-wording check does not otherwise need.
        throw errorCode switch
        {
            NavInsertDeniedPermissionException.InsertDeniedErrorCode =>
                new NavInsertDeniedPermissionException(message),
            NavDeleteDeniedPermissionException.DeleteDeniedErrorCode =>
                new NavDeleteDeniedPermissionException(message),
            _ => new NavModifyDeniedPermissionException(message),
        };
#pragma warning restore CS0618
    }
}
