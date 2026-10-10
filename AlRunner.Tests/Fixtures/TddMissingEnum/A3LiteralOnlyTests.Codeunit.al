/// <summary>An enum named only by `Enum::"X"::Value`, with no variable of the type, is AL0118 (not AL0185): nothing
/// generates it, and the file is FAILED naming that diagnostic.</summary>
codeunit 65322 "Missing Enum Literal Only"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure LiteralOnly_IsRefused()
    var
        I: Integer;
    begin
        I := Enum::"Only Literal Enum"::Open.AsInteger();
    end;
}
