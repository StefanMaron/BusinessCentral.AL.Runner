/// <summary>The implementing app: declares Existing(A) only, and none of the members the tests name.</summary>
codeunit 65201 "Tdd Shape Target Cu"
{
    procedure Existing(A: Integer): Integer
    begin
        exit(A);
    end;
}
