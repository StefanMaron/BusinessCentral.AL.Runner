/// <summary>#5265: calls "M4", which "Lib Chain 3" does not declare, while each library calls a member the one before it lacks.</summary>
codeunit 72040 "Lib Chain Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Reaches()
    var
        Last: Codeunit "Lib Chain 3";
        Result: Integer;
    begin
        Result := Last.M4(1);
    end;
}
