codeunit 65410 "Idp Codeunit Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingCodeunit_TakesAFreeId()
    var
        M: Codeunit "Idp Missing Codeunit";
        X: Integer;
    begin
        X := M.Calc(5);
    end;
}
