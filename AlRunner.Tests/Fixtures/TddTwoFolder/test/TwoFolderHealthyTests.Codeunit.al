/// <summary>
/// References nothing missing, so it must pass in the same run.
/// </summary>
codeunit 65221 "Two Folder Healthy Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Unrelated_StillPasses()
    var
        Tier: Enum "Two Folder Tier";
    begin
        Tier := "Two Folder Tier"::Gold;
        if Tier <> "Two Folder Tier"::Gold then
            Error('enum value did not round-trip');
    end;
}
