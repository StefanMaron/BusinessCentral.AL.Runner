/// <summary>#5287: the test bundle of two sibling libraries (lib4 and lib4b) that call the same missing member, so one of their compiles generates it and the other finds it already generated.</summary>
codeunit 72090 "Lib Chain Both Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Reaches()
    var
        Last: Codeunit "Lib Chain 4";
        Result: Integer;
    begin
        Result := Last.M5(1);
    end;
}
