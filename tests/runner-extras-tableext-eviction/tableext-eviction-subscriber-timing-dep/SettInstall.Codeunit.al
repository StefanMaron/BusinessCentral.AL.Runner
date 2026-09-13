/// Forces "Sett T"'s NCLMetaTable to be built and both subscribers in "Sett Subscribers" to be
/// injected onto it (RecordPatches.NclMetaTableBuilder.BuildNCLMetaTable -> EventSubscriberPatches
/// .InjectTriggerSubsForTable / InjectValidateSubsForTable, via the standard `Record X` local-var
/// construction chokepoint) DURING THIS APP'S OWN install phase -- before the sibling "test" app's
/// tableextension on "Sett T" is even parsed. See app.json for the full #2510 writeup.
codeunit 65522 "Sett Install"
{
    Subtype = Install;

    trigger OnInstallAppPerDatabase()
    var
        Rec: Record "Sett T";
    begin
        Rec.Init();
        Rec."No." := 'SEED';
        Rec.Insert(true);
        Rec.Validate(Val, 1);
    end;
}
