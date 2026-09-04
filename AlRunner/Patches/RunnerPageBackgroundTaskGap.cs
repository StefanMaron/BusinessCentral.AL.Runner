// RunnerPageBackgroundTaskGap — run page background tasks INLINE against the current session.
//
// WHY
//   CurrPage.EnqueueBackgroundTask (from an AL page trigger) and TestPage.RunPageBackgroundTask
//   both run their child task synchronously in BC's own test framework
//   (PageBackgroundTask.CanPageBackgroundTaskRunAsync is false without a real service-tier
//   scheduler) — decompiled from Ncl.dll 28.4.53241.54183:
//
//     internal static void NavForm.EnqueueBackgroundTask(NavSession session, PageBackgroundTask
//     task, IPageBackgroundTaskCompletionTrigger trigger = null)
//     {
//         var runtime = new NavChildSessionTaskRuntime<PageBackgroundChildSessionTask>(task.ChildSessionTask, session);
//         if (PageBackgroundTask.CanPageBackgroundTaskRunAsync(session)) { ...schedule async...; return; }
//         task.TaskCompletionTrigger = trigger ?? new PageBackgroundTaskCompletionImmediateTrigger(task);
//         runtime.RunAsync(runtime.ChildSessionTask.CancellationToken, useParentsSqlDiagnostics: true)
//             .AsTask().GetAwaiter().GetResult();
//     }
//
//   and NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask, bool) has the same shape via its
//   own `new NavChildSessionTaskRuntime<PageBackgroundChildSessionTask>(task.ChildSessionTask,
//   NavCurrentThread.Session).RunAsync(...).AsTask().GetAwaiter().GetResult()`.
//
//   BOTH real call sites already take the synchronous branch under the runner
//   (CanPageBackgroundTaskRunAsync is false whenever session.ClientConnectionType.IsHeadlessClient()
//   — true for the runner's HeadlessClientCallback — or session.TestExecution.InTest, both always
//   true here), so BC's OWN code already runs this inline rather than scheduling real async work.
//   The only thing that does not work in-process is what NavChildSessionTaskRuntime<T>.RunAsync
//   does past that point: it creates a brand-new NavSession and really Open()s/OpenCompanyAsync()s
//   it (a full service-tier session/company bootstrap: SQL connection state, tenant database
//   bring-up, permission-manager resolution) purely to isolate the child task from the parent
//   session — an isolation guarantee the AL-observable contract does not depend on (see below).
//
//   AfterRunTaskAsync/AfterRunTaskErrorAsync — the calls that raise OnPageBackgroundTaskCompleted /
//   OnPageBackgroundTaskError — are ALREADY invoked by real BC against base.ParentSession, not the
//   child session (see NavChildSessionTaskRuntime<T>.RunAsync's
//   `await base.ChildSessionTask.AfterRunTaskAsync(base.ParentSession, linkedToken);`). Only the
//   worker codeunit body itself (RunTaskInChildSessionAsync) runs "in the child session" — and BC's
//   own PageBackgroundTaskCompletionImmediateTrigger.RunTaskCompletionAsync explicitly asserts
//   `if (session == null) throw new InvalidOperationException("The immediate trigger must be
//   called on the parent session...")`, i.e. BC's own synchronous-trigger contract is written in
//   terms of the PARENT session throughout. So running the worker codeunit directly against the
//   CURRENT (parent) session — instead of manufacturing an isolated child NavSession just to
//   immediately discard it — reproduces the observable AL contract faithfully: same in-memory
//   table state (there is only one, in the runner), same Page.GetBackgroundParameters() /
//   Page.SetBackgroundTaskResult() protocol (NavSession.PageBackgroundTask, set by
//   PageBackgroundChildSessionTask.RunTaskInChildSessionAsync itself), and the SAME completion-
//   trigger dispatch BC's own unmodified NavForm.RaiseOnPageBackgroundTaskCompletedAsync /
//   RaiseOnPageBackgroundTaskErrorAsync perform. See issue #2514 for the differential against a
//   real BC 28.4 container that measured this: enqueued results visible before OpenView/
//   GoToRecord return, RunPageBackgroundTask(..., false) returning the worker's dictionary.
//
// Two Cecil-rewritten call sites route here (see NclCecilRewrite.cs), replacing what used to be a
// call into NavChildSessionTaskRuntime<PageBackgroundChildSessionTask>.RunAsync (an async state
// machine method this cannot safely rewrite directly — see precompiled-dll-respect.md's guidance
// to prefer the simplest faithful mechanism):
//   - NavForm.EnqueueBackgroundTask(NavSession, PageBackgroundTask, IPageBackgroundTaskCompletionTrigger)
//   - NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask, bool)
using System;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner.Patches;

public static class RunnerPageBackgroundTaskGap
{
    /// <summary>
    /// Replaces NavForm.EnqueueBackgroundTask(NavSession, PageBackgroundTask,
    /// IPageBackgroundTaskCompletionTrigger) — the static dispatcher CurrPage.EnqueueBackgroundTask
    /// funnels through. Mirrors that method's own real-BC synchronous branch (see header) minus the
    /// child-session bootstrap.
    /// </summary>
    public static void EnqueueBackgroundTaskInline(
        NavSession session, PageBackgroundTask task, IPageBackgroundTaskCompletionTrigger trigger)
    {
        // PageBackgroundTask.TaskCompletionTrigger has an `internal` setter in Ncl.dll, not
        // visible to AlRunner at compile time (no InternalsVisibleTo grant to Ncl.dll's own
        // assembly, unlike the AlRunner.Tests grant PrecompileSupport.cs documents) — reflection
        // is the established pattern in this file's siblings for that shape.
        _taskCompletionTriggerProperty.SetValue(task, trigger ?? new PageBackgroundTaskCompletionImmediateTrigger(task));
        RunChildSessionTaskInline(session, task);
    }

    private static readonly System.Reflection.PropertyInfo _taskCompletionTriggerProperty =
        typeof(PageBackgroundTask).GetProperty(nameof(PageBackgroundTask.TaskCompletionTrigger))
        ?? throw new InvalidOperationException("PageBackgroundTask.TaskCompletionTrigger not found — Ncl shape changed; do not commit");

    /// <summary>
    /// Replaces NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask, bool) — the internal
    /// static helper both TestPage.RunPageBackgroundTask() overloads funnel through. The caller
    /// (NavTestPage.ALRunPageBackgroundTask(int, ByRef&lt;...&gt;, bool)) already set
    /// task.TaskCompletionTrigger before reaching here, exactly as in real BC.
    /// </summary>
    public static NavDictionary<NavText, NavText> RunPageBackgroundTaskInline(
        PageBackgroundTask task, bool throwChildSessionError)
    {
        var session = NavCurrentThread.Session;
        RunChildSessionTaskInline(session, task);
        if (throwChildSessionError && task.Error != null)
            throw task.Error.Exception;
        return task.Results;
    }

    /// <summary>
    /// The shared core both dispatch points funnel through in real BC
    /// (NavChildSessionTaskRuntime&lt;PageBackgroundChildSessionTask&gt;.RunAsync), reproduced
    /// against the current session instead of an isolated, freshly-opened child NavSession.
    /// </summary>
    private static void RunChildSessionTaskInline(NavSession session, PageBackgroundTask task)
    {
        var childSessionTask = task.ChildSessionTask;
        childSessionTask.Status = NavChildSessionTaskStatus.Running;

        bool proceed = SyncWait(childSessionTask.BeforeRunTaskAsync(session, NavCancellationToken.None));
        if (!proceed)
            return; // matches real BC: BeforeRunTaskAsync==false means preconditions not met, no After* fires

        NavChildSessionTaskError? error = null;
        try
        {
            SyncWait(childSessionTask.RunTaskInChildSessionAsync(session, NavCancellationToken.None));
        }
        catch (NavBaseException ex)
        {
            error = new NavChildSessionTaskError(session, ex);
        }

        if (error == null)
        {
            SyncWait(childSessionTask.AfterRunTaskAsync(session, NavCancellationToken.None));
            childSessionTask.Status = NavChildSessionTaskStatus.Completed;
        }
        else
        {
            // task.Error and task.Results are set INSIDE AfterRunTaskErrorAsync by BC's own
            // (unmodified) PageBackgroundChildSessionTask.AfterRunTaskErrorAsync body — do not
            // duplicate that here.
            SyncWait(childSessionTask.AfterRunTaskErrorAsync(session, NavCancellationToken.None, error));
            childSessionTask.Status = NavChildSessionTaskStatus.Error;
        }
    }

    private static void SyncWait(System.Threading.Tasks.ValueTask vt) => vt.AsTask().GetAwaiter().GetResult();
    private static T SyncWait<T>(System.Threading.Tasks.ValueTask<T> vt) => vt.AsTask().GetAwaiter().GetResult();
}
