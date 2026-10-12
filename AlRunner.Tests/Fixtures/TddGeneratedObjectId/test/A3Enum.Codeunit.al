codeunit 65412 "Idp Enum Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingEnum_TakesAFreeId()
    var
        K: Enum "Idp Missing Enum";
    begin
        if K.AsInteger() <> 0 then
            Error('not 0');
    end;
}
