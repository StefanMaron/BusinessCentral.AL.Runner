codeunit 93101 "RRS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RRS Src";

    // ------------------------------------------------------------------
    // IsDirty — always false in standalone (no dirty tracking)
    // ------------------------------------------------------------------

    [Test]
    procedure RecordRef_IsDirty_ReturnsFalse()
    var
        Rec: Record "RRS Table";
        RecRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        Assert.IsFalse(Src.GetIsDirty(RecRef), 'IsDirty should return false for unmodified RecordRef');
    end;

    // ------------------------------------------------------------------
    // LoadFields — no-op; AreFieldsLoaded still returns true
    // ------------------------------------------------------------------

    [Test]
    procedure RecordRef_LoadFields_IsNoOp()
    var
        Rec: Record "RRS Table";
        RecRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        Src.CallLoadFields(RecRef, 1);
        Assert.IsTrue(RecRef.AreFieldsLoaded(1), 'AreFieldsLoaded should return true after LoadFields');
    end;

    // ------------------------------------------------------------------
    // CopyLinks — no-op, must not throw
    // ------------------------------------------------------------------

    [Test]
    procedure RecordRef_CopyLinks_IsNoOp()
    var
        Rec: Record "RRS Table";
        RecRef: RecordRef;
        FromRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        FromRef.GetTable(Rec);
        Src.CallCopyLinks(RecRef, FromRef);
    end;

    // ------------------------------------------------------------------
    // ReadConsistency — false in standalone (no SQL read-consistency)
    // ------------------------------------------------------------------

    [Test]
    procedure RecordRef_ReadConsistency_ReturnsFalse()
    var
        Rec: Record "RRS Table";
        RecRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        Assert.IsFalse(Src.GetReadConsistency(RecRef), 'ReadConsistency should return false in standalone mode');
    end;

    // ------------------------------------------------------------------
    // SecurityFiltering — roundtrip: set Filtered, get Filtered back
    // ------------------------------------------------------------------

    [Test]
    procedure RecordRef_SecurityFiltering_RoundTrip()
    var
        Rec: Record "RRS Table";
        RecRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        Src.SetSecurityFiltering(RecRef, SecurityFilter::Filtered);
        Assert.AreEqual(SecurityFilter::Filtered, Src.GetSecurityFiltering(RecRef),
            'SecurityFiltering should return the value that was set');
    end;

    // ------------------------------------------------------------------
    // Truncate — clears all in-memory rows
    // ------------------------------------------------------------------

    [Test]
    procedure RecordRef_Truncate_ClearsAllRows()
    var
        Rec: Record "RRS Table";
        RecRef: RecordRef;
        i: Integer;
    begin
        // Insert 3 rows
        for i := 1 to 3 do begin
            Rec.Init();
            Rec.Id := i;
            Rec.Name := 'Row ' + Format(i);
            Rec.Insert();
        end;

        RecRef.Open(Database::"RRS Table");
        Assert.AreEqual(3, RecRef.Count(), 'Should have 3 rows before Truncate');

        Src.CallTruncate(RecRef);
        Assert.AreEqual(0, RecRef.Count(), 'Truncate should clear all rows');
    end;
}
