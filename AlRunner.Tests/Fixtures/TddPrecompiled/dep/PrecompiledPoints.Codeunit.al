codeunit 65300 "Precompiled Points"
{
    procedure Existing(): Integer
    begin
        exit(7);
    end;

    procedure Twice(Value: Integer): Integer
    begin
        exit(Value * 2);
    end;
}
