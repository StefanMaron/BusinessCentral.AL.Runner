codeunit 65406 "Lib Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure CallsCalc()
    var
        L: Codeunit Lib;
        X: Integer;
    begin
        X := L.Calc(5);
    end;
}
