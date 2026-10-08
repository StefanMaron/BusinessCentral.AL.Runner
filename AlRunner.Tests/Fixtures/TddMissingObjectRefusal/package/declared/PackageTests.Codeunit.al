/// <summary>#5446 control: the app declares its dependency on the package, so "Package Only Points" resolves to the
/// package's codeunit (no AL0185) and --tdd generates nothing.</summary>
codeunit 65380 "Missing Obj Package Declared"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure DeclaredPackageObject_IsNotGenerated()
    var
        Points: Codeunit "Package Only Points";
    begin
    end;
}
