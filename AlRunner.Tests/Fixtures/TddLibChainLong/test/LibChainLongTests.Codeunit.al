/// <summary>#5287: calls "M5", which "Lib Chain 4" does not declare, while each library calls a member the one before it lacks.</summary>
codeunit 72060 "Lib Chain Long Tests"
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
