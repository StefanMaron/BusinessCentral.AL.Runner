/// <summary>The same table named from a second file, and a codeunit and a table that share one name.</summary>
codeunit 65321 "Missing Table Second File"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SameTable_InASecondFile()
    var
        T: Record "No Such Table";
    begin
        T."Amount" := 7;
        T.Insert();
    end;

    [Test]
    procedure TableAndCodeunit_ShareOneName()
    var
        Twin: Codeunit "Twin";
        Row: Record "Twin";
        Result: Integer;
    begin
        Row."Weight" := 2;
        Row.Insert();
        Result := Twin.Calc(4);
    end;
}
