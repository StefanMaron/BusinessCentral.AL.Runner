/// <summary>
/// References Archived, an enum value "Tdd Target Enum" does not declare yet.
/// Without --tdd this whole object is excluded from the emit. With --tdd the value is
/// generated, the test passes, and its result names Archived (#5147).
/// </summary>
codeunit 65012 "Tdd Broken Enum Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingEnumValue_RunsAgainstGeneratedValue()
    var
        E: Enum "Tdd Target Enum";
    begin
        E := E::Archived;
    end;
}
