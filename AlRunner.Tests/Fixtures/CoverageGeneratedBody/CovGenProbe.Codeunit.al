/// <summary>Line numbers are pinned by AlRunner.Tests/CoverageTests.cs.</summary>
codeunit 50940 "Cov Gen Probe RXT"
{
    procedure RaiseInside(Value: Integer): Text
    begin
        asserterror
        begin
            Value := Value + 1;
            Error('raised %1', Value);
        end;
        exit(GetLastErrorText());
    end;
}
