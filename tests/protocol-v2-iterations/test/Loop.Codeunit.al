codeunit 59905 LoopTest
{
    Subtype = Test;

    [Test]
    procedure RunsLoop()
    var
        i: Integer;
        sum: Integer;
    begin
        for i := 1 to 3 do
            sum += i;
        if sum <> 6 then Error('expected 6');
    end;
}
