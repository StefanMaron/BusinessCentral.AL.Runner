codeunit 1315001 "Format Option Helper"
{
    procedure FormatStyle(Style: Option Standard,Attention,Favorable): Text
    begin
        exit(Format(Style));
    end;

    procedure FormatStyleEqualsLiteral(Style: Option Standard,Attention,Favorable; Literal: Text): Boolean
    begin
        exit(Format(Style) = Literal);
    end;
}
