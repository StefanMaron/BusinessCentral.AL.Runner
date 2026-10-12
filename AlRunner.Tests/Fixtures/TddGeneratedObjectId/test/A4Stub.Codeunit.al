codeunit 65413 "Idp Stub Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure StubBesidePackageCodeunit_TakesAFreeId()
    var
        P: Codeunit "Idp Calc";
        X: Integer;
    begin
        X := P.Missing(5);
    end;
}
