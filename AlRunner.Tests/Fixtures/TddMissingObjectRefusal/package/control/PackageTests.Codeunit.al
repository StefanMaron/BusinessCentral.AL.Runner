/// <summary>#5446 control: the package in .alpackages is there, but no app or package declares "Nowhere Declared
/// Points", so --tdd still generates it and the test runs to the stub.</summary>
codeunit 65380 "Missing Obj Package Control"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingEverywhere_StillGenerated()
    var
        Points: Codeunit "Nowhere Declared Points";
        Result: Integer;
    begin
        Result := Points.CalcPoints(1);
    end;
}
