/// <summary>
/// References "Loyalty Points", a field "Tdd Target Table" does not declare yet.
/// Without --tdd this whole object is excluded from the emit. With --tdd the field is
/// generated, the test passes, and its result names "Loyalty Points" (#5147).
/// </summary>
codeunit 65011 "Tdd Broken Field Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingField_RunsAgainstGeneratedField()
    var
        Rec: Record "Tdd Target Table";
    begin
        Rec."Loyalty Points" := 5;
    end;
}
