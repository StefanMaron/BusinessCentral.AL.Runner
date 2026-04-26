codeunit 56580 "HL Probe"
{
    procedure OpenDoc(): Integer
    begin
        Hyperlink('https://example.com/docs');
        exit(42);
    end;

    procedure OpenDocMessage(Msg: Text): Integer
    begin
        Hyperlink('https://example.com/docs?m=' + Msg);
        exit(99);
    end;
}
