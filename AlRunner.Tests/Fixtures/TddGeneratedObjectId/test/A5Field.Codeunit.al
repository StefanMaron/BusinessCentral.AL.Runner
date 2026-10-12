codeunit 65414 "Idp Field Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure FieldBesidePackageTable_TakesAFreeId()
    var
        E: Record "Idp Entry";
    begin
        E.Points := 5;
    end;
}
