codeunit 55300 "Var Attribute Helper"
{
    var
        [Protected]
        ProtectedInt: Integer;
        [Protected]
        ProtectedText: Text[50];
        [InternallyVisible]
        InternalInt: Integer;
        [InternallyVisible]
        InternalText: Text[100];

    procedure SetProtectedInt(Value: Integer)
    begin
        ProtectedInt := Value;
    end;

    procedure GetProtectedInt(): Integer
    begin
        exit(ProtectedInt);
    end;

    procedure SetProtectedText(Value: Text[50])
    begin
        ProtectedText := Value;
    end;

    procedure GetProtectedText(): Text[50]
    begin
        exit(ProtectedText);
    end;

    procedure SetInternalInt(Value: Integer)
    begin
        InternalInt := Value;
    end;

    procedure GetInternalInt(): Integer
    begin
        exit(InternalInt);
    end;

    procedure SetInternalText(Value: Text[100])
    begin
        InternalText := Value;
    end;

    procedure GetInternalText(): Text[100]
    begin
        exit(InternalText);
    end;
}
