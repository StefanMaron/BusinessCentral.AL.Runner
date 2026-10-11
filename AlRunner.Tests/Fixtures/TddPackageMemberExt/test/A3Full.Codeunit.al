/// <summary>#5491: the other package holds every id of the range, so nothing can be generated and the file fails as before.</summary>
codeunit 65402 "Ext Full Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure FullField_IsRefused()
    var
        E: Record "Full Entry";
    begin
        E.Id := 1;
        E.Fulled := 7;
    end;

    [Test]
    procedure FullValue_IsRefused()
    var
        T: Enum "Full Tier";
    begin
        T := "Full Tier"::Fulled;
    end;
}
