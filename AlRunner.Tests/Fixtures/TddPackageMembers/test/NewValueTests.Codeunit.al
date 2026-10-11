codeunit 65401 "Package Value Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NewValue_IsNotTheDefault()
    var
        T: Enum "Loyalty Tier";
    begin
        T := "Loyalty Tier"::Gold;
        if T = "Loyalty Tier"::Bronze then
            Error('Gold must not be Bronze');
    end;
}
