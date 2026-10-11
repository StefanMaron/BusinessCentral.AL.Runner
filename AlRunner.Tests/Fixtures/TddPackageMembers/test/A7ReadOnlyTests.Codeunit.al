/// <summary>"Unassigned" is only read, never assigned: nothing fixes its type, so it is not generated.</summary>
codeunit 65406 "Package Read Only Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure FieldOnlyRead_IsRefused()
    var
        E: Record "Loyalty Entry";
    begin
        if E.Unassigned <> 0 then
            Error('unexpected');
    end;
}
