/// <summary>The implementing app: declares Existing(A) only.</summary>
codeunit 65300 "Overload Calc"
{
    procedure Existing(A: Integer): Integer
    begin
        exit(A);
    end;
}
