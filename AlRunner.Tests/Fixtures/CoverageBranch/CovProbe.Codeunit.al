/// <summary>
/// One `if`/`else` with a known taken branch (Flag=true -> Result:=2) and a known
/// untaken branch (Result:=3) — proves the "did not execute" half of --coverage is not
/// vacuous. Statement line numbers are pinned by AlRunner.Tests/CoverageTests.cs.
/// </summary>
codeunit 50921 "Cov Probe RXT"
{
    procedure Run(Flag: Boolean): Integer
    var
        Result: Integer;
    begin
        Result := 1;
        if Flag then begin
            Result := 2;
        end else begin
            Result := 3;
        end;
        exit(Result);
    end;
}
