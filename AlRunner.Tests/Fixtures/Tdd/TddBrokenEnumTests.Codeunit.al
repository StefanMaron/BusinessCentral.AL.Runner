/// <summary>
/// References Archived, an enum value "Tdd Target Enum" does not declare yet.
/// Without --tdd this whole object is excluded from the emit. With --tdd it must
/// report FAILED, naming Archived.
/// </summary>
codeunit 65012 "Tdd Broken Enum Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingEnumValue_ReportsFailedNotVanished()
    var
        E: Enum "Tdd Target Enum";
    begin
        E := E::Archived;
    end;
}
