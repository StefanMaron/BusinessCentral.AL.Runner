// Source codeunit for issue #1092 — AlScope.Parent static property.
//
// When the BC compiler emits code for scope classes, it may reference
// AlScope.Parent as a static member access (CS0117). The fix adds a
// static null-returning stub on AlScope so that such generated code compiles.
//
// This source codeunit exercises a pattern where scope classes access
// codeunit-level variables through the Parent reference.
codeunit 60290 "Scope Parent Source"
{
    var
        StepCounter: Integer;
        LastStep: Text[50];

    procedure ExecuteStep(StepName: Text[50])
    begin
        StepCounter += 1;
        LastStep := StepName;
    end;

    procedure GetStepCounter(): Integer
    begin
        exit(StepCounter);
    end;

    procedure GetLastStep(): Text[50]
    begin
        exit(LastStep);
    end;

    procedure Reset()
    begin
        StepCounter := 0;
        LastStep := '';
    end;
}
