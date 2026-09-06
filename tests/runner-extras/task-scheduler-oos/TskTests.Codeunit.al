// Issue #2866 — the runner's refusal on the task-scheduler surface, and its scope.
//
// Background job scheduling against a real scheduler is permanently out of scope
// (.claude/rules/loud-failures.md, docs/scope.md §3.6). Before this suite, two of the five
// AL entry points did not refuse at all: TaskExists reached BC's scheduler SQL layer and
// died with `NullReferenceException` inside NavSqlConnectionScope.TryOpenConnection, and
// CancelTask died the same way inside ALCancelTaskAsync. An NRE out of BC's data layer
// names no API and cites no doc, so an AL author has no way to learn which surface they
// touched — which is exactly what loud-failures.md exists to prevent.
//
// The two scoping controls at the bottom are load-bearing. CreateTask and SetTaskReady
// already refuse in BC's OWN words, because BC's real body checks CanCreateTask (which the
// runner Cecil-rewrites to false) and raises NavCreateScheduledTasksNotAllowedException.
// That is real BC behaviour and #1739 decided deliberately to keep it. An over-broad fix
// that refused the whole ALTaskScheduler type by name would pass the two tests above and
// silently replace BC's answer with the runner's.
codeunit 65603 "Tsk Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Tsk Assert";
        NotAllowedErr: Label 'You do not have permission to create or run scheduled tasks.', Locked = true;

    [Test]
    procedure TaskExists_RefusedByNameNotNre()
    var
        TaskId: Guid;
        Exists: Boolean;
    begin
        TaskId := CreateGuid();

        asserterror Exists := TaskScheduler.TaskExists(TaskId);

        // The API the AL author wrote, spelled as AL spells it — not ALTaskExistsAsync,
        // and not a bare "out-of-scope".
        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');
        Assert.ExpectedError('task-scheduler');
        // The anchor has to land on the row that actually covers this surface.
        Assert.ExpectedError('docs/scope.md#jobs');
        Assert.ErrorContainsExactlyOnce('see docs/scope.md');
        // The regression this issue is about: BC's data layer must never be reached.
        Assert.NotExpectedError('Object reference not set');
    end;

    [Test]
    procedure CancelTask_RefusedByNameNotNre()
    var
        TaskId: Guid;
        Cancelled: Boolean;
    begin
        TaskId := CreateGuid();

        asserterror Cancelled := TaskScheduler.CancelTask(TaskId);

        Assert.ExpectedError('out-of-scope: TaskScheduler.CancelTask');
        Assert.ExpectedError('task-scheduler');
        Assert.ExpectedError('docs/scope.md#jobs');
        Assert.ErrorContainsExactlyOnce('see docs/scope.md');
        Assert.NotExpectedError('Object reference not set');
    end;

    [Test]
    procedure CancelTask_EmptyId_AnswersFalseAndIsNotRefused()
    var
        EmptyTaskId: Guid;
    begin
        // Scoping control for CancelTask_RefusedByNameNotNre, and the reason that refusal is
        // not a blanket one. BC's real ALCancelTaskAsync opens with
        // `if (task == Guid.Empty) return false;` — an answer it reaches before touching the
        // scheduled-task store or the scheduler, so the runner can give it faithfully and
        // refusing here would fail AL over a surface that was never involved. The same
        // short-circuit opens SetTaskReady, which is not rewritten at all; both paths keep
        // the same invariant.
        Clear(EmptyTaskId);

        Assert.IsFalse(TaskScheduler.CancelTask(EmptyTaskId),
            'BC answers an empty task id false without a scheduler, and so must the runner');
    end;

    [Test]
    procedure CanCreateTask_AnswersFalseAndIsNotRefused()
    begin
        // Scoping control. CanCreateTask is the guard the documented pattern
        // (`if TaskScheduler.CanCreateTask() then …`) is built on, so it has to keep
        // ANSWERING rather than throwing — a refusal here would make the guard itself
        // unusable and take the clean path away with the broken one.
        Assert.IsFalse(TaskScheduler.CanCreateTask(),
            'the runner has no scheduler, so CanCreateTask is false — and it answers, it does not refuse');
    end;

    [Test]
    procedure CreateTask_KeepsBcsOwnRefusalNotTheRunners()
    var
        State: Codeunit "Tsk State";
        TaskId: Guid;
    begin
        // Scoping control (#1733, #1739): BC's own body raises this once CanCreateTask is
        // false. Substituting the runner's out-of-scope refusal here would be a divergence
        // from real BC for no gain, and would invalidate the divergence entry the corpus
        // test TaskScheduler_CreateTask_InsideTest_ReturnsNonEmptyGuid is classified under.
        asserterror TaskId := TaskScheduler.CreateTask(Codeunit::"Tsk Target", 0, true, CompanyName(), 0DT);

        Assert.ExpectedError(NotAllowedErr);
        Assert.NotExpectedError('out-of-scope:');
        Assert.IsFalse(State.DidRun(), 'docs/scope.md §3.6: a scheduled task''s codeunit is never executed');
    end;

    [Test]
    procedure SetTaskReady_KeepsBcsOwnRefusalNotTheRunners()
    var
        TaskId: Guid;
        SetReady: Boolean;
    begin
        // Same control for the other member whose real body already refuses through
        // CanCreateTask. docs/limitations.md claimed until #2866 that SetTaskReady
        // "completes without error, having done nothing" — measured, it never did.
        TaskId := CreateGuid();

        asserterror SetReady := TaskScheduler.SetTaskReady(TaskId);

        Assert.ExpectedError(NotAllowedErr);
        Assert.NotExpectedError('out-of-scope:');
    end;
}
