/// <summary>A Text argument fixes no length, so "Fire" is not generated: the member stays missing and this file is
/// FAILED naming it. The object itself is still generated for the other files.</summary>
codeunit 65324 "Missing Object Refused Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TextArgument_IsRefused()
    var
        Missing: Codeunit "No Such Codeunit";
        Result: Integer;
    begin
        Result := Missing.Fire('text');
    end;
}
