/// <summary>
/// docs/scope.md §3.6: no scheduler is available headlessly. ALTaskScheduler.CanCreateTask
/// is made to faithfully return false, and unguarded TaskScheduler.CreateTask is meant to hit
/// BC's own body throwing NavCreateScheduledTasksNotAllowedException (not a runner substitute).
/// This is a deliberate runner-scoping decision, not adjudicable real-BC behaviour (a real BC
/// server's CanCreateTask answer depends on live authentication/license context) -- it belongs
/// here, not in the upstream corpus.
/// </summary>
codeunit 64301 "TSC Tests"
{
    Subtype = Test;

    var
        TscAssert: Codeunit "TSC Assert";

    [Test]
    procedure CanCreateTask_ReturnsFalse()
    begin
        // The headless runner has no scheduler: scope.md §3.6 documents CanCreateTask as
        // faithfully false so guarded AL (`if TaskScheduler.CanCreateTask then ...`) skips
        // creation cleanly.
        TscAssert.IsFalse(TaskScheduler.CanCreateTask(), 'CanCreateTask must be false: the runner has no scheduler.');
    end;

    [Test]
    procedure CreateTask_Unguarded_ThrowsBCsOwnNotAllowedException_NotACodeunitResolutionError()
    begin
        // Regression for #1733: before the fix, ALTaskScheduler.CheckCodeUnit ran BC's real
        // (unpatched) body first -- because its no-op was registered on the disabled JmpHook
        // layer, never the live Cecil layer -- and threw a codeunit-resolution NavALException
        // naming this very test codeunit's own id, never reaching the documented
        // NavCreateScheduledTasksNotAllowedException gate.
        asserterror TaskScheduler.CreateTask(CODEUNIT::"TSC Tests", 0, true);

        // BC's own NavCreateScheduledTasksNotAllowedException resource string (Lang.
        // ScheduledTasksNotAllowed). A codeunit-resolution NavALException instead would read
        // "...CodeUnit object with the ID... does not exist..." -- a different message entirely,
        // so this pins the exact documented exception, not merely "something failed".
        TscAssert.IsTrue(
            GetLastErrorText() = 'You do not have permission to create or run scheduled tasks.',
            StrSubstNo('Expected BC''s NavCreateScheduledTasksNotAllowedException text, got: %1', GetLastErrorText()));
    end;
}
