/// <summary>An enum written as an id is refused by name, whatever the packages hold: the test is FAILED on the AL0185.</summary>
codeunit 65362 "Missing Enum Id Name"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure EnumWrittenAsAnId_IsRefused()
    var
        K: Enum "65999";
    begin
    end;
}
