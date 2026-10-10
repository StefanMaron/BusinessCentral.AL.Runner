/// <summary>The same enum named from a second file, and a codeunit and an enum that share one name.</summary>
codeunit 65321 "Missing Enum Second File"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SameEnum_InASecondFile()
    var
        K: Enum "No Such Enum";
    begin
        K := K::Closed;
        if K.AsInteger() <> 1 then
            Error('Closed is %1, expected 1', K.AsInteger());
    end;

    [Test]
    procedure EnumAndCodeunit_ShareOneName()
    var
        Twin: Codeunit "Twin";
        State: Enum "Twin";
        Result: Integer;
    begin
        State := State::Alpha;
        Result := Twin.Calc(4);
    end;
}
