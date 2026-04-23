codeunit 302002 "Clear Methods Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Clear Methods Helper";
        Assert: Codeunit Assert;

    // ── Issue #1181: Clear(OutStream) ─────────────────────────────────────────

    [Test]
    procedure OutStream_Clear_IsNoOp()
    var
        OS: OutStream;
    begin
        // [GIVEN] An OutStream variable (default/uninitialized state)
        // [WHEN] Clear is called on the OutStream
        // [THEN] No exception is raised — Clear is a no-op for OutStream
        Clear(OS); // must not throw CS1061
    end;

    [Test]
    procedure OutStream_Clear_ViaHelper_IsNoOp()
    var
        OS: OutStream;
    begin
        // [GIVEN] An OutStream variable backed by a Blob
        Helper.GetOutStream(OS);

        // [WHEN] Clear is called via a helper (var parameter path)
        // [THEN] No exception is raised
        Helper.ClearOutStream(OS);
    end;

    // ── Issue #1182: Clear(File) ──────────────────────────────────────────────

    [Test]
    procedure File_Clear_IsNoOp()
    var
        F: File;
    begin
        // [GIVEN] A File variable (default state)
        // [WHEN] Clear is called on the File
        // [THEN] No exception is raised — Clear is a no-op for File
        Clear(F); // must not throw CS1061
    end;

    [Test]
    procedure File_Clear_ViaHelper_IsNoOp()
    var
        F: File;
    begin
        // [GIVEN] A File variable
        // [WHEN] Clear is called via a helper (var parameter path)
        // [THEN] No exception is raised
        Helper.ClearFile(F);
    end;

    // ── Issue #1178: Clear(RecordArray[i]) ────────────────────────────────────

    [Test]
    procedure RecordArrayElement_Clear_ResetsFields()
    var
        RecArr: array[3] of Record "CMH Record";
    begin
        // [GIVEN] A record array element with a specific field value
        Helper.SetRecordArrayField(RecArr, 1, 42);
        Assert.AreEqual(42, Helper.GetRecordArrayField(RecArr, 1), 'Field should be 42 before Clear');

        // [WHEN] Clear is called on an array element
        Helper.ClearRecordArrayElement(RecArr, 1);

        // [THEN] The field is reset to its default value (0 for Integer)
        Assert.AreEqual(0, Helper.GetRecordArrayField(RecArr, 1), 'Field should be 0 after Clear');
    end;

    [Test]
    procedure RecordArrayElement_Clear_DirectCall_ResetsFields()
    var
        RecArr: array[3] of Record "CMH Record";
    begin
        // [GIVEN] Array element 2 has value 99
        Helper.SetRecordArrayField(RecArr, 2, 99);
        Assert.AreEqual(99, Helper.GetRecordArrayField(RecArr, 2), 'Field should be 99 before Clear');

        // [WHEN] Clear called directly on the array element
        Clear(RecArr[2]);

        // [THEN] Field is reset to 0; element 3 is unaffected (still 0)
        Assert.AreEqual(0, Helper.GetRecordArrayField(RecArr, 2), 'Field should be 0 after Clear(RecArr[2])');
        Assert.AreEqual(0, Helper.GetRecordArrayField(RecArr, 3), 'Uncleared element should remain 0');
    end;

    [Test]
    procedure RecordArrayElement_Clear_OnlyAffectsTargetIndex()
    var
        RecArr: array[3] of Record "CMH Record";
    begin
        // [GIVEN] All elements have values
        Helper.SetRecordArrayField(RecArr, 1, 10);
        Helper.SetRecordArrayField(RecArr, 2, 20);
        Helper.SetRecordArrayField(RecArr, 3, 30);

        // [WHEN] Clear element 2 only
        Clear(RecArr[2]);

        // [THEN] Element 2 is reset; elements 1 and 3 are untouched
        Assert.AreEqual(10, Helper.GetRecordArrayField(RecArr, 1), 'Element 1 should remain 10');
        Assert.AreEqual(0, Helper.GetRecordArrayField(RecArr, 2), 'Element 2 should be reset to 0');
        Assert.AreEqual(30, Helper.GetRecordArrayField(RecArr, 3), 'Element 3 should remain 30');
    end;
}
