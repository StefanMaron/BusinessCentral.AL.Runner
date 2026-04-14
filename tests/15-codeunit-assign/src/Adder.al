codeunit 50115 "Adder"
{
    procedure Add(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;

    procedure AddThree(A: Integer; B: Integer; C: Integer): Integer
    begin
        exit(A + B + C);
    end;
}
