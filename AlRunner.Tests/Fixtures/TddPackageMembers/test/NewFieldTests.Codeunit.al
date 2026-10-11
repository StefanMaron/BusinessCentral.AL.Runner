/// <summary>#5430: "Loyalty Entry" arrives as a package and has no "Points" field.</summary>
codeunit 65400 "Package Field Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NewField_RoundTripsThroughTheTable()
    var
        E: Record "Loyalty Entry";
        Read: Record "Loyalty Entry";
    begin
        E.Id := 1;
        E.Points := 5;
        E.Insert();
        Read.Get(1);
        if Read.Points <> 5 then
            Error('Expected 5, got %1', Read.Points);
    end;

    [Test]
    procedure ExistingField_IsThePackagesOwn()
    var
        E: Record "Loyalty Entry";
    begin
        E.Id := 2;
        E."Existing Points" := 9;
        E.Insert();
        E.Get(2);
        if E."Existing Points" <> 9 then
            Error('Expected 9, got %1', E."Existing Points");
    end;
}
