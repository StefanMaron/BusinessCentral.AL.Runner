/// Minimal report stub for Report.ALAssign coverage (issue #1328).
report 307200 "RAS Report"
{
    dataset { }
}

/// Source codeunit exercising Report := Report assignment (ALAssign).
/// BC compiler lowers Rep1 := Rep2 to Rep1.ALAssign(Rep2) on MockReportHandle.
codeunit 307201 "RAS Src"
{
    /// Assign one report variable to another and run the assigned variable.
    /// This exercises the Rep1 := Rep2 path which emits ALAssign on the handle.
    procedure AssignAndRun(): Boolean
    var
        Rep1: Report "RAS Report";
        Rep2: Report "RAS Report";
    begin
        Rep1 := Rep2;   // BC compiler emits Rep1.ALAssign(Rep2)
        exit(true);
    end;

    /// Assign twice to ensure the handle state is properly replaced each time.
    procedure AssignTwice(): Boolean
    var
        Rep1: Report "RAS Report";
        Rep2: Report "RAS Report";
        Rep3: Report "RAS Report";
    begin
        Rep1 := Rep2;
        Rep1 := Rep3;
        exit(true);
    end;
}
