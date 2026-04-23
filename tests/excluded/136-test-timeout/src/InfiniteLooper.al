codeunit 59960 "Infinite Looper"
{
    procedure LoopForever()
    var
        i: Integer;
    begin
        repeat
            i += 1;
        until false;
    end;
}
