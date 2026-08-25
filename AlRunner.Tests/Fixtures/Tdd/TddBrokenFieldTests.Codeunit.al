/// <summary>
/// References "Loyalty Points", a field "Tdd Target Table" does not declare yet.
/// Without --tdd this whole object is excluded from the emit. With --tdd it must
/// report FAILED, naming "Loyalty Points".
/// </summary>
codeunit 65011 "Tdd Broken Field Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingField_ReportsFailedNotVanished()
    var
        Rec: Record "Tdd Target Table";
    begin
        Rec."Loyalty Points" := 5;
    end;
}
