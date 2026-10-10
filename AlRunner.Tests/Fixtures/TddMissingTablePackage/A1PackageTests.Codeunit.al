/// <summary>#5445: "Package Only Ledger" is declared by a package of .alpackages that app.json does not depend on, so an
/// empty table of that name would shadow it: refused, and the test is FAILED on the AL0185.</summary>
codeunit 65360 "Missing Table Package Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure UndeclaredPackageTable_IsNotShadowed()
    var
        Ledger: Record "Package Only Ledger";
    begin
        Ledger.Init();
    end;
}
