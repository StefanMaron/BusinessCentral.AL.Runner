// Issue #3236. Reading Object Metadata inside an install trigger is what puts the table in the
// install baseline: the capture walks every store that exists when install seeding ends, and
// the first access to 2000000071 is what creates this one and fills it with synthesised rows.
codeunit 65581 "OMBR Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        if ObjectMetadata.IsEmpty() then
            Error('Object Metadata must already hold its synthesised rows inside an install trigger.');
    end;
}
