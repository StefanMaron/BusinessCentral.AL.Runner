/// Codeunit with an OnRun trigger and a reader procedure so tests can verify
/// that `.Run()` on a codeunit variable actually fires the trigger.
codeunit 59570 "CIR Processor"
{
    trigger OnRun()
    begin
        ProcessedValue := 42;
        WasRun := true;
    end;

    procedure GetValue(): Integer
    begin
        exit(ProcessedValue);
    end;

    procedure DidRun(): Boolean
    begin
        exit(WasRun);
    end;

    var
        ProcessedValue: Integer;
        WasRun: Boolean;
}

/// Codeunit that takes an instance-bearing codeunit variable by reference
/// and calls `.Run()` on it — the exact pattern issue #474 names.
codeunit 59571 "CIR Src"
{
    procedure RunAndGetValue(var Proc: Codeunit "CIR Processor"): Integer
    begin
        Proc.Run();
        exit(Proc.GetValue());
    end;

    procedure RunAndGetDidRun(var Proc: Codeunit "CIR Processor"): Boolean
    begin
        Proc.Run();
        exit(Proc.DidRun());
    end;

    procedure GetValueWithoutRun(var Proc: Codeunit "CIR Processor"): Integer
    begin
        exit(Proc.GetValue());
    end;
}
