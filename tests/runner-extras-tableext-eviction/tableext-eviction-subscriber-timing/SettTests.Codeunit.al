/// Regression suite for issue #2510. See dep/app.json and app.json for the full mechanism
/// writeup. Ordering is forced, not hoped for: the "dep" app's Install-subtype codeunit builds
/// "Sett T"'s NCLMetaTable and injects both event subscribers during ITS OWN install, before
/// this app's tableextension (which evicts+rebuilds "Sett T"'s NCLMetaTable) is even parsed.
codeunit 65527 "Sett Tests"
{
    Subtype = Test;

    [Test]
    procedure SubscribersFireAfterTableExtEviction()
    var
        Rec: Record "Sett T";
    begin
        // "Sett T" was already built+wired (subscribers injected) during the "dep" app's own
        // install (see dep/SettInstall.Codeunit.al) and then evicted+rebuilt by this app's
        // tableextension (SettExt.TableExt.al) parsing during AddSourceDir. Without the #2510
        // fix, the rebuilt NCLMetaTable's new event scopes carry neither subscription at all,
        // so InsertFlag stays false and Computed stays 0.
        Rec.Init();
        Rec."No." := 'SETT1';
        Rec.Insert(true);
        Rec.Validate(Val, 5);

        if not Rec.InsertFlag then
            Error('Assert failed: expected InsertFlag = true (OnBeforeInsertEvent subscriber must still fire after tableextension eviction)');
        if Rec.Computed <> 15 then
            Error('Assert failed: expected Computed = 15 (OnAfterValidateEvent subscriber must still fire after tableextension eviction), got %1', Rec.Computed);
    end;

    [Test]
    procedure UnrelatedRecordWithoutEviction_SubscribersFireNormally()
    var
        Rec: Record "Sett T";
    begin
        // Negative control: a second, independent record on the SAME (rebuilt) table must
        // behave identically -- rules out a fix that only patches the first Insert/Validate
        // call rather than the table's subscriber wiring itself.
        Rec.Init();
        Rec."No." := 'SETT2';
        Rec.Insert(true);
        Rec.Validate(Val, 7);

        if not Rec.InsertFlag then
            Error('Assert failed: expected InsertFlag = true');
        if Rec.Computed <> 21 then
            Error('Assert failed: expected Computed = 21, got %1', Rec.Computed);
    end;
}
