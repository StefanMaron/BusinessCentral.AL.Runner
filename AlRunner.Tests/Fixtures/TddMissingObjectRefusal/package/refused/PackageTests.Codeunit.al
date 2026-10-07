/// <summary>#5446: "Package Only Points" is a real codeunit of a package in this app's .alpackages, but app.json
/// declares no dependency on that package, so it is unresolved here (AL0185). An empty codeunit of that name would
/// shadow it: the first test would pass against nothing.</summary>
codeunit 65380 "Missing Obj Package Tests"
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
}
