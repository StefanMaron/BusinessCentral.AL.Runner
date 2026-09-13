/// Regression suite for issue #2463. See dep/app.json and app.json for the full mechanism
/// writeup. Ordering is forced, not hoped for: the "dep" app's Install-subtype codeunit
/// materializes a "Tefte T" record during ITS OWN install, before this app's tableextension
/// (which evicts+rebuilds "Tefte T"'s NCLMetaTable) is even parsed.
codeunit 65277 "Tefte Tests"
{
    Subtype = Test;

    [Test]
    procedure ValidateAfterTableExtEviction_FieldTriggerStillFires()
    var
        Rec: Record "Tefte T";
    begin
        // "Tefte T" was already built+wired during the "dep" app's own install (see
        // dep/TefteInstall.Codeunit.al) and then evicted+rebuilt by this app's
        // tableextension (TefteExt.TableExt.al) parsing during AddSourceDir. Without the
        // #2463 fix, the rebuilt NCLMetaTable's field 2 carries no ValidateHandler at all,
        // so Validate just sets the field and "Computed" stays 0.
        Rec.Init();
        Rec."No." := 'TEFTE1';
        Rec.Insert(true);
        Rec.Validate(Val, 5);

        if Rec.Computed <> 10 then
            Error('Assert failed: expected Computed = 10 (Val OnValidate trigger must still fire after tableextension eviction), got %1', Rec.Computed);
    end;

    [Test]
    procedure UnrelatedRecordWithoutEviction_FieldTriggerFiresNormally()
    var
        Rec: Record "Tefte T";
    begin
        // Negative control: a second, independent record on the SAME (rebuilt) table must
        // behave identically -- rules out a fix that only patches the first Validate call
        // rather than the table's wiring itself.
        Rec.Init();
        Rec."No." := 'TEFTE2';
        Rec.Insert(true);
        Rec.Validate(Val, 7);

        if Rec.Computed <> 14 then
            Error('Assert failed: expected Computed = 14, got %1', Rec.Computed);
    end;
}
