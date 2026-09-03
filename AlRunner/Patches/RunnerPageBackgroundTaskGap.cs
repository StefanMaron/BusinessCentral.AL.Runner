// RunnerPageBackgroundTaskGap — loud refusal for page background task SYNCHRONOUS execution.
//
// WHY
//   CurrPage.EnqueueBackgroundTask (from an AL page trigger) and TestPage.RunPageBackgroundTask
//   both run their child task synchronously in BC's own test framework
//   (PageBackgroundTask.CanPageBackgroundTaskRunAsync is false without a real service-tier
//   scheduler), which opens a REAL child NavSession and really Open()s/OpenCompanyAsync()s it
//   (NavChildSessionTaskRuntime&lt;T&gt;.RunAsync). That is a full service-tier session/company
//   bootstrap — SQL connection state, tenant database bring-up, permission-manager resolution —
//   the runner's in-process, no-SQL skeleton cannot faithfully answer.
//
//   Three real skeleton gaps on the way in were found and fixed (see NclCecilRewrite.cs and
//   MetadataPatches.cs, all still in place): NavTenant.Diagnostics was never seeded (the
//   originally-reported ArgumentNullException on CurrPage.EnqueueBackgroundTask);
//   NavTestPageBase.get_ServerForm() handed PageBackgroundTask's ctor a tree-less uninitialised
//   NavForm (the originally-reported "Parent.Tree cannot be null" on
//   TestPage.RunPageBackgroundTask); NavTenant.CanCreateSession's real body assumes a live
//   SQL-backed tenant. Past those three, NavSession.Open()'s real body reaches a
//   NavNCLConnectionNotOpenedException from inside the child session's own execution — a
//   further, deeper layer (a service-tier connection/DataAccessSource bootstrap) whose exact
//   throw site was not pinned down. See docs/scope.md and issue #2514 for the full trail.
//
// Per loud-failures.md: an in-scope surface the runner cannot yet faithfully support must
// refuse LOUDLY, naming the AL-visible API, never silently misbehave or half-execute.
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static class RunnerPageBackgroundTaskGap
{
    /// <summary>
    /// Called from the Cecil-rewritten NavForm.EnqueueBackgroundTask(NavSession,
    /// PageBackgroundTask, IPageBackgroundTaskCompletionTrigger) — the static dispatcher
    /// CurrPage.EnqueueBackgroundTask funnels through.
    /// </summary>
    public static System.Exception ThrowEnqueueBackgroundTaskNotYetImplemented()
        => new RunnerOutOfScopeException(
            "Page.EnqueueBackgroundTask",
            "not-yet-implemented — page background tasks run synchronously in BC's own test "
            + "framework, which requires a real child-session/company connection bootstrap "
            + "(NavSession.Open -> OpenCompanyAsync) the runner's in-process skeleton does not "
            + "yet support; see issue #2514",
            "todo");

    /// <summary>
    /// Called from the Cecil-rewritten NavTestPage.ALRunPageBackgroundTask(PageBackgroundTask,
    /// bool) — the internal static helper both TestPage.RunPageBackgroundTask() overloads
    /// funnel through.
    /// </summary>
    public static System.Exception ThrowRunPageBackgroundTaskNotYetImplemented()
        => new RunnerOutOfScopeException(
            "TestPage.RunPageBackgroundTask",
            "not-yet-implemented — page background tasks run synchronously in BC's own test "
            + "framework, which requires a real child-session/company connection bootstrap "
            + "(NavSession.Open -> OpenCompanyAsync) the runner's in-process skeleton does not "
            + "yet support; see issue #2514",
            "todo");
}
