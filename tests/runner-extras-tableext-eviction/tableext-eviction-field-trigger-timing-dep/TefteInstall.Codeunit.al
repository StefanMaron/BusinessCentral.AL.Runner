/// Forces "Tefte T"'s NCLMetaTable to be built and its field OnValidate trigger wired
/// (RecordPatches.WireFieldTriggerHandlersForTable, via the standard `Record X` local-var
/// construction chokepoint) DURING THIS APP'S OWN install phase -- before the sibling "test"
/// app's tableextension on "Tefte T" is even parsed. See app.json for the full #2463 writeup.
codeunit 65271 "Tefte Install"
{
    Subtype = Install;

    trigger OnInstallAppPerDatabase()
    var
        Rec: Record "Tefte T";
    begin
        Rec.Init();
        Rec."No." := 'SEED';
        Rec.Insert(true);
    end;
}
