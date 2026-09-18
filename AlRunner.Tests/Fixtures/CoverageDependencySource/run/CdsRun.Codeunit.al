// #4272. The execute-handler counterpart of main/CdsTests.Codeunit.al: OnRun calls the
// sibling SOURCE dependency's Twice() and never its Never(), so the same two controls apply
// to HandleServerExecute's coverage map as to the CLI's.
codeunit 70880 "CDS Run"
{
    trigger OnRun()
    var
        Subject: Codeunit "CDS Subject";
        Actual: Integer;
    begin
        Actual := Subject.Twice(21);
        if Actual <> 42 then
            Error('expected 42, actual %1', Actual);
    end;

    // #4272: never called, and deliberately not a [Test]. The consumer-side twin of the
    // dependency's Never(). The server's statement table lists only scopes that recorded a hit
    // (AlCoverageTracker.GetHitTrackedTypes), so this is absent from it exactly as Never() is —
    // and that symmetry is the control: it says the dependency is treated like any other parsed
    // root, not dumped wholesale.
    procedure NeverInRunConsumer(Value: Integer): Integer
    begin
        exit(Value * 5);
    end;
}
