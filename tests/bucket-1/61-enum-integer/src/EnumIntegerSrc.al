enum 57700 "EI Color"
{
    Extensible = false;

    value(0; Red) { Caption = 'Red'; }
    value(1; Green) { Caption = 'Green'; }
    value(2; Blue) { Caption = 'Blue'; }
}

codeunit 57701 "EI Enum Converter"
{
    procedure ToInteger(C: Enum "EI Color"): Integer
    begin
        exit(C.AsInteger());
    end;

    procedure FromInteger(I: Integer): Enum "EI Color"
    begin
        exit(Enum::"EI Color".FromInteger(I));
    end;
}
