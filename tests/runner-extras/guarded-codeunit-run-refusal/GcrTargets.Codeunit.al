// The codeunits a guarded Codeunit.Run executes. Each is run through BOTH spellings of the
// call — the static `Codeunit.Run(Codeunit::X)` and a codeunit variable's own `Run()` — because
// the runner replaces them with two different methods.

// A runner refusal by name: task scheduling is permanently out of scope (docs/scope.md#jobs).
codeunit 65741 "Gcr Refuses"
{
    trigger OnRun()
    var
        Exists: Boolean;
    begin
        Exists := TaskScheduler.TaskExists(CreateGuid());
    end;
}

// The other refusal flavour: an in-scope surface the runner cannot answer, raised from inside
// BC's own code rather than from a runner patch (the #5190 backstop).
codeunit 65742 "Gcr Refuses From Bc"
{
    trigger OnRun()
    var
        Rec: RecordRef;
        KeyRef: KeyRef;
    begin
        Rec.Open(Database::"Gcr Probe Tbl");
        KeyRef := Rec.KeyIndex(2);
        Database.AlterKey(KeyRef, false);
    end;
}

// A plain AL error: the case a guarded run exists to trap.
codeunit 65743 "Gcr Errors"
{
    trigger OnRun()
    begin
        Error('plain AL error');
    end;
}

// An error BC itself raises, not an Error() call.
codeunit 65744 "Gcr Bc Error"
{
    trigger OnRun()
    var
        Row: Record "Gcr Probe Tbl";
    begin
        Row.Get(424242);
    end;
}

// A CLR DivideByZeroException that BC turns into an AL error before anything can catch it.
codeunit 65745 "Gcr Divides"
{
    trigger OnRun()
    var
        Zero: Integer;
        Result: Integer;
    begin
        Result := 1 div Zero;
    end;
}

// Raises nothing: the control that a guarded run still answers true.
codeunit 65746 "Gcr Quiet"
{
    trigger OnRun()
    begin
    end;
}

// Runs the refusing codeunit through a guarded run of its own, so the refusal has to cross a
// second guarded run on its way out.
codeunit 65747 "Gcr Nested"
{
    trigger OnRun()
    var
        Ok: Boolean;
    begin
        Ok := Codeunit.Run(Codeunit::"Gcr Refuses");
    end;
}

table 65748 "Gcr Probe Tbl"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[30]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
        key(ByName; Name) { }
    }
}
