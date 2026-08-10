codeunit 62141 "EMSR Tests"
{
    // Regression for issue #1719: Page "Error Messages" (System Application page 700), used
    // as a plain page variable, NREd inside its own precompiled SetRecords body:
    //
    //   procedure SetRecords(var TempErrorMessage: Record "Error Message" temporary)
    //   begin
    //       if TempErrorMessage.FindFirst() then;
    //       if TempErrorMessage.IsTemporary then
    //           Rec.Copy(TempErrorMessage, true)
    //       else
    //           TempErrorMessage.CopyToTemp(Rec);
    //   end;
    //
    // Root cause: a plain `Page "Error Messages"` variable is built via
    // NavFormHandle.CreateTarget, which (before the fix) constructed the page through the
    // single-arg ITreeObject ctor and never bound a source record — so `Rec` on the page
    // instance stayed null, and `Rec.Copy(...)` NREd. The TestPage construction path
    // (RunnerPageInstance.TryCreate) already selects the two-arg (ITreeObject, NavRecord)
    // ctor and binds a real record; the page-variable path needs the same treatment.
    //
    // RED (before fix): both tests below throw NullReferenceException at the SetRecords call.
    // GREEN (after fix): SetRecords runs its real Base App body and the temp record set
    // handed to it round-trips correctly, for both a populated set and an empty one — so a
    // fix keyed on "the set happens to be non-empty" cannot pass both directions.
    Subtype = Test;

    var
        Assert: Codeunit "EMSR Assert";

    [Test]
    procedure SetRecords_StoresTheTempRecordSetOnThePageInstance()
    var
        TempErrorMessage: Record "Error Message" temporary;
        ErrorMessagesPage: Page "Error Messages";
    begin
        // [GIVEN] A temporary Error Message set with one row.
        TempErrorMessage.ID := 1;
        TempErrorMessage."Message" := CopyStr('EMSR probe message', 1, MaxStrLen(TempErrorMessage."Message"));
        TempErrorMessage.Insert();

        // [WHEN] SetRecords hands the set to the page. Before the fix this NREs inside the
        // unmodified Base App body (Rec.Copy(TempErrorMessage, true) against a null Rec).
        ErrorMessagesPage.SetRecords(TempErrorMessage);

        // [THEN] The caller's own temporary set must survive the handover untouched — BC's
        // real body branches on TempErrorMessage.IsTemporary and copies INTO Rec, never
        // mutating the caller's variable.
        Assert.AreEqual(1, TempErrorMessage.Count(),
            'Caller''s temporary Error Message set must still hold its row after SetRecords.');
        Assert.IsTrue(TempErrorMessage.Get(1),
            'Caller''s temporary Error Message row 1 must still be readable after SetRecords.');
        Assert.AreEqualText('EMSR probe message', TempErrorMessage."Message",
            'Caller''s Error Message text must be unchanged after SetRecords.');
    end;

    [Test]
    procedure SetRecords_AcceptsAnEmptyTempRecordSet()
    var
        TempErrorMessage: Record "Error Message" temporary;
        ErrorMessagesPage: Page "Error Messages";
    begin
        // [GIVEN] No rows at all — the negative-direction companion. A fix keyed on "the set
        // is non-empty" (e.g. only binding Rec when FindFirst succeeds) would pass the test
        // above and still NRE, or silently swallow, here.
        Assert.IsTrue(TempErrorMessage.IsEmpty(), 'A freshly declared temporary Error Message set must start empty.');

        // [WHEN]
        ErrorMessagesPage.SetRecords(TempErrorMessage);

        // [THEN] Still empty, no exception.
        Assert.AreEqual(0, TempErrorMessage.Count(),
            'Caller''s temporary Error Message set must still be empty after SetRecords of an empty set.');
    end;
}
