/// <summary>"Ghost" would be generated into a tableextension appended to this file, but "When" has no assignment that types it, so
/// the file does not compile and is dropped, the extension with it.</summary>
codeunit 65410 "Package Ghost Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure GhostField_HomeFileIsDropped()
    var
        E: Record "Ghost Entry";
    begin
        E.Id := 1;
        E.Ghost := 1;
        E.When := Today;
    end;
}
