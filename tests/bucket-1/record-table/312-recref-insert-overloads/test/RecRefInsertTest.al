codeunit 312003 "RecRef Insert Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -------------------------------------------------------------------------
    // RecordRef.Insert(Boolean) — 1-arg overload
    // -------------------------------------------------------------------------

    [Test]
    procedure RecRefInsert_Bool_TriggerTrue_RecordExistsAndMarkerSet()
    // Proves Insert(true) persists the record and fires OnInsert (which sets Marker).
    var
        Helper: Codeunit "RecRef Insert Helper";
    begin
        Helper.InsertViaRecRefBool(1, true);

        Assert.IsTrue(Helper.RecordExists(1), 'Record should exist after RecordRef.Insert(true)');
        Assert.AreEqual('triggered', Helper.GetMarker(1), 'OnInsert should have set Marker to ''triggered''');
    end;

    [Test]
    procedure RecRefInsert_Bool_TriggerFalse_RecordExistsMarkerBlank()
    // Proves Insert(false) persists the record but skips OnInsert (Marker stays blank).
    var
        Helper: Codeunit "RecRef Insert Helper";
    begin
        Helper.InsertViaRecRefBool(2, false);

        Assert.IsTrue(Helper.RecordExists(2), 'Record should exist after RecordRef.Insert(false)');
        Assert.AreEqual('', Helper.GetMarker(2), 'OnInsert must NOT fire when RunTrigger=false');
    end;

    // -------------------------------------------------------------------------
    // RecordRef.Insert(Boolean, Boolean) — 2-arg overload
    // -------------------------------------------------------------------------

    [Test]
    procedure RecRefInsert_BoolBool_TriggerTrue_RecordExistsAndMarkerSet()
    // Proves Insert(true, false) compiles and persists the record, firing OnInsert.
    var
        Helper: Codeunit "RecRef Insert Helper";
    begin
        Helper.InsertViaRecRefBoolBool(3, true, false);

        Assert.IsTrue(Helper.RecordExists(3), 'Record should exist after RecordRef.Insert(true, false)');
        Assert.AreEqual('triggered', Helper.GetMarker(3), 'OnInsert should have set Marker to ''triggered'' with Insert(true,false)');
    end;

    [Test]
    procedure RecRefInsert_BoolBool_TriggerFalse_RecordExistsMarkerBlank()
    // Proves Insert(false, true) compiles and skips OnInsert.
    var
        Helper: Codeunit "RecRef Insert Helper";
    begin
        Helper.InsertViaRecRefBoolBool(4, false, true);

        Assert.IsTrue(Helper.RecordExists(4), 'Record should exist after RecordRef.Insert(false, true)');
        Assert.AreEqual('', Helper.GetMarker(4), 'OnInsert must NOT fire when RunTrigger=false in Insert(false,true)');
    end;
}
