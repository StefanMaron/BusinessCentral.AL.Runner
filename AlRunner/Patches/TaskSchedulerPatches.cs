// TaskSchedulerPatches — the loud refusal on BC's ALTaskScheduler surface (#2866).
//
// docs/scope.md §3.6 (anchor `jobs`) puts background job scheduling against a real
// scheduler permanently out of scope, and .claude/rules/loud-failures.md requires such a
// surface to refuse by name rather than fail some other way. Measured on BC 28.1 before
// this file existed, three of the five AL entry points behaved and two did not:
//
//   CanCreateTask()  false        — Cecil-rewritten, faithful (the runner cannot schedule)
//   CreateTask()     BC's own NavCreateScheduledTasksNotAllowedException — deliberate,
//                                  #1733 / #1739; BC's real body raises it once
//                                  CanCreateTask is false, and we do not substitute it
//   SetTaskReady()   the same BC refusal, via the same guard in its real body
//   TaskExists()     NullReferenceException from NavSqlConnectionScope.TryOpenConnection,
//                    reached through NavTaskScheduler.SqlDml.RetrySqlAsync
//   CancelTask()     NullReferenceException from inside ALCancelTaskAsync itself
//
// The last two have no CanCreateTask guard in their real bodies — BC just goes to the
// scheduled-task store — so BC never refuses on our behalf and the runner has to. An NRE
// out of BC's data layer names no API and cites no doc; an AL author reading it cannot
// tell which surface they touched or that the runner has a documented position on it.
//
// So both are rewritten to throw RunnerOutOfScopeException naming the AL-visible API and
// citing docs/scope.md#jobs. The other three are deliberately left exactly as they are:
// CanCreateTask must keep ANSWERING (it is the guard the documented `if
// TaskScheduler.CanCreateTask() then …` pattern is built on — refusing there would take
// the working path away along with the broken one), and CreateTask / SetTaskReady already
// refuse in BC's own words, which is the answer the corpus test
// TaskScheduler_CreateTask_InsideTest_ReturnsNonEmptyGuid is classified against in
// tests/expectations/divergence-session.json.
//
// RED → GREEN: tests/runner-extras/task-scheduler-oos.

using System;
using System.Threading.Tasks;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// Body replacements for the two <c>ALTaskScheduler</c> members the runner refuses. Each
/// stands in for the whole async method, so it carries that method's
/// <c>ValueTask&lt;bool&gt;</c> return type and BC's own argument list; the Cecil rewrite
/// forwards the arguments and returns whatever comes back.
/// </summary>
public static class TaskSchedulerPatches
{
    // Reason and anchor are separate arguments on purpose. RunnerOutOfScopeException
    // appends " — see docs/scope.md#<anchor>" itself, so a reason that also spelled the
    // doc out would render it twice — the defect #2766 tracks across ~45 existing call
    // sites. tests/runner-extras/task-scheduler-oos asserts "see docs/scope.md" appears
    // exactly once, so this cannot regress quietly.
    private const string Anchor = "jobs";

    private const string ExistsReason =
        "task-scheduler — the runner has no scheduler and no scheduled-task store, so it cannot "
        + "say whether a task exists; BC's real body queries the scheduled-task table over a SQL "
        + "connection that does not exist here. Nothing is ever scheduled: CanCreateTask() is false "
        + "and CreateTask() is refused by BC's own guard, so no task can have been created either";

    private const string CancelReason =
        "task-scheduler — the runner has no scheduler and no scheduled-task store, so there is "
        + "nothing to cancel and no way to record that a cancellation happened; BC's real body "
        + "deletes the scheduled-task row and queues a cancel against the service tier's scheduler";

    /// <summary>
    /// Replacement for <c>ALTaskScheduler.ALTaskExistsAsync(NavSession, Guid)</c>, which the
    /// sync <c>ALTaskExists(Guid)</c> wrapper also funnels through.
    ///
    /// <para>Unconditional: BC's real body has no early answer to preserve. It goes straight
    /// to the scheduled-task store for every task id, including <c>Guid.Empty</c>, so there
    /// is no id the runner can answer for without a scheduler.</para>
    /// </summary>
    public static ValueTask<bool> ALTaskExistsAsync_Replacement(object? session, Guid task)
        => throw new RunnerOutOfScopeException("TaskScheduler.TaskExists", ExistsReason, Anchor);

    /// <summary>
    /// Replacement for <c>ALTaskScheduler.ALCancelTaskAsync(NavSession, Guid)</c>, which the
    /// sync <c>ALCancelTask(Guid)</c> wrapper also funnels through.
    ///
    /// <para>Conditional, unlike its sibling. BC's real body opens with
    /// <c>if (task == Guid.Empty) return false;</c> — an answer it reaches before touching
    /// the scheduled-task store or the scheduler, and one this runner can therefore give
    /// faithfully. Refusing there would be over-broad: it would turn AL that BC answers
    /// deterministically into a hard failure over a surface that was never involved. The
    /// short-circuit is byte-identical in Ncl on BC 27.0 through 28.4 (compared, not
    /// assumed), so this is not a version-specific reading.</para>
    ///
    /// <para>Its sibling <c>ALSetTaskReadyAsync</c> opens with the same empty-id check and is
    /// not rewritten at all, so the two paths keep the same invariant.</para>
    /// </summary>
    public static ValueTask<bool> ALCancelTaskAsync_Replacement(object? session, Guid task)
    {
        if (task == Guid.Empty)
            return new ValueTask<bool>(false);
        throw new RunnerOutOfScopeException("TaskScheduler.CancelTask", CancelReason, Anchor);
    }
}
