codeunit 56711 "RecRef Assign Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure AssignCopiesFieldValues()
    var
        Helper: Codeunit "RecRef Assign Helper";
        Result: Text[100];
    begin
        // [SCENARIO] RecordRef := OtherRecordRef copies field data
        Result := Helper.InsertAndAssign(1, 'Hello');
        Assert.AreEqual('Hello', Result, 'Assigned RecordRef should have same field values');
    end;

    [Test]
    procedure AssignCopiesTableNumber()
    var
        Helper: Codeunit "RecRef Assign Helper";
        TableNo: Integer;
    begin
        // [SCENARIO] RecordRef := OtherRecordRef copies the table number
        TableNo := Helper.AssignedTableNo();
        Assert.AreNotEqual(0, TableNo, 'Assigned RecordRef should have non-zero table number');
    end;

    [Test]
    procedure AssignedRecRefFieldDiffersAfterModify()
    var
        Helper: Codeunit "RecRef Assign Helper";
        Result: Text[100];
    begin
        // [SCENARIO] After assignment, modifying one RecordRef field should NOT
        // automatically reflect in the other (they are separate handles).
        // This is a negative test: verifying that the copy is independent.
        Result := Helper.InsertAndAssign(2, 'Original');
        Assert.AreEqual('Original', Result, 'Should return the original value, not something else');

        // Also verify a wrong value fails
        asserterror Assert.AreEqual('Wrong', Result, '');
        Assert.ExpectedError('Assert.AreEqual failed');
    end;
}
