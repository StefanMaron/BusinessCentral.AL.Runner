codeunit 65411 "Idp Table Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingTable_TakesAFreeId()
    var
        T: Record "Idp Missing Table";
    begin
        T.Init();
    end;
}
