/// <summary>A second extensible package enum, "Alpha Tier", which sorts before "Loyalty Tier": enums are taken in name order,
/// so it gets the first free enumextension id whatever order the compile reported anything in.</summary>
codeunit 65409 "Package Alpha Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SecondEnum_GetsItsOwnExtension()
    var
        T: Enum "Alpha Tier";
    begin
        T := "Alpha Tier"::Extra;
        if T = "Alpha Tier"::Zero then
            Error('Extra must not be Zero');
    end;
}
