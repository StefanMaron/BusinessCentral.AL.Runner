/// <summary>"Points" is also assigned in A1FieldTests, which sorts first: the tableextension lives there, once, and this
/// file compiles against the same field.</summary>
codeunit 65405 "Package Second File Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SameField_InASecondFile()
    var
        E: Record "Loyalty Entry";
    begin
        E.Id := 3;
        E.Points := 11;
        E.Insert();
        E.Get(3);
        if E.Points <> 11 then
            Error('Expected 11, got %1', E.Points);
    end;
}
