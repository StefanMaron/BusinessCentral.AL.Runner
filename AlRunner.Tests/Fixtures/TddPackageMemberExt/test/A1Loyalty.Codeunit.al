/// <summary>#5491: "Loyalty Entry" and "Loyalty Tier" arrive as a package and lack "Points" and "Gold"; another package's
/// extensions already add field 65401 and value 65401, in the ids this app may allocate from.</summary>
codeunit 65400 "Ext Loyalty Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NewField_TakesAFreeId()
    var
        E: Record "Loyalty Entry";
    begin
        E.Points := 5;
        if E.FieldNo(Points) <> 65402 then
            Error('Expected 65402, got %1', E.FieldNo(Points));
    end;

    [Test]
    procedure NewValue_TakesAFreeOrdinal()
    var
        T: Enum "Loyalty Tier";
    begin
        T := "Loyalty Tier"::Gold;
        if T.AsInteger() <> 65402 then
            Error('Expected 65402, got %1', T.AsInteger());
    end;
}
