/// Helper codeunit that wraps Byte.ToText() calls.
codeunit 50156 "BTT Helper"
{
    procedure ByteToText(B: Byte): Text
    begin
        exit(B.ToText());
    end;

    procedure IsEmpty(B: Byte): Boolean
    begin
        exit(B.ToText() = '');
    end;
}
