/// <summary>#5450: two missing codeunits in one run. "Package Only Points" is declared by a package of .alpackages that
/// app.json does not depend on (refused), "Zulu Nowhere Points" by nobody (generated). The refused name comes first in
/// name order, and must not use up the id the generated one takes.</summary>
codeunit 65380 "Missing Obj Package Reserve"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure UndeclaredPackageObject_IsNotShadowed()
    var
        Points: Codeunit "Package Only Points";
        Result: Integer;
    begin
        Result := Points.CalcPoints(1);
    end;

    [Test]
    procedure MissingEverywhere_IsGenerated()
    var
        Zulu: Codeunit "Zulu Nowhere Points";
        Result: Integer;
    begin
        Result := Zulu.CalcPoints(1);
    end;
}
