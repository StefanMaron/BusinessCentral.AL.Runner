/// Exercises Debugger.IsActive and Debugger.DeactivateDebugger — the two
/// that are most likely to appear in production AL (test helpers).
codeunit 60370 "DBG Src"
{
    procedure IsDebuggerActive(): Boolean
    begin
        exit(Debugger.IsActive());
    end;

    procedure DeactivateDoesNotThrow(): Boolean
    begin
        Debugger.Deactivate();
        exit(true);
    end;

    procedure ActivateDoesNotThrow(): Boolean
    begin
        Debugger.Activate();
        exit(true);
    end;
}
