codeunit 65415 "Idp Value Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ValueBesidePackageEnum_TakesAFreeId()
    var
        T: Enum "Idp Tier";
    begin
        T := "Idp Tier"::Gold;
    end;
}
