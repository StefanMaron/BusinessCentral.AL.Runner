// #4448: the control on the Microsoft-floor carve-out of app-group visibility. This group declares
// neither `application` nor any dependency; the other groups of the combined runner-extras run
// reach Base Application through theirs. A floor app is installed in every tenant, so its objects
// stay listed here. The mechanism is docs/virtual-tables-allobj.md#precompiled-package-visibility.
codeunit 66341 "AGV Floor Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "AGV Floor Assert";

    [Test]
    procedure AllObj_MicrosoftFloorTable_StaysVisible()
    var
        AllObj: Record AllObj;
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsTrue(AllObj.Get(AllObj."Object Type"::Table, 289), 'AllObj must list Base Application table 289, which the Microsoft floor supplies to every app group');
        Assert.IsTrue(TableMetadata.Get(289), 'Table Metadata must list Base Application table 289, which the Microsoft floor supplies to every app group');
    end;
}
