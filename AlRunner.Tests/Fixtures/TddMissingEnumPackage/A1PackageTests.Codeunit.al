/// <summary>#5445: "Package Only Status" is declared by a package of .alpackages that app.json does not depend on, so an
/// empty enum of that name would shadow it: refused, and the test is FAILED on the AL0185.</summary>
codeunit 65360 "Missing Enum Package Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure UndeclaredPackageEnum_IsNotShadowed()
    var
        Status: Enum "Package Only Status";
    begin
        Status := Status::Open;
    end;
}
