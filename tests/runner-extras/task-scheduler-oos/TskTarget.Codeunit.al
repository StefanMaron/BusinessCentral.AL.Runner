// Target for the CreateTask scoping control below. It must never run: docs/scope.md §3.6
// says tasks are never executed, and CreateTask is refused by BC's own guard long before
// a scheduler would pick this up. It sets a flag so the control can say so out loud.
codeunit 65601 "Tsk Target"
{
    trigger OnRun()
    var
        State: Codeunit "Tsk State";
    begin
        State.MarkRan();
    end;
}

codeunit 65602 "Tsk State"
{
    SingleInstance = true;

    var
        Ran: Boolean;

    procedure MarkRan()
    begin
        Ran := true;
    end;

    procedure DidRun(): Boolean
    begin
        exit(Ran);
    end;
}
