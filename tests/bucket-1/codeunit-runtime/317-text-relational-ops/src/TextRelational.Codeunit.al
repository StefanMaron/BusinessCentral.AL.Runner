codeunit 1314003 "Text Relational Helper"
{
    procedure GreaterText(A: Text; B: Text): Boolean
    begin
        exit(A > B);
    end;

    procedure LessText(A: Text; B: Text): Boolean
    begin
        exit(A < B);
    end;

    procedure GreaterOrEqualText(A: Text; B: Text): Boolean
    begin
        exit(A >= B);
    end;

    procedure LessOrEqualText(A: Text; B: Text): Boolean
    begin
        exit(A <= B);
    end;

    procedure NotEqualText(A: Text; B: Text): Boolean
    begin
        exit(A <> B);
    end;
}
