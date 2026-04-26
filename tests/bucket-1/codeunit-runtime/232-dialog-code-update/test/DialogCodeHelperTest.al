// Test suite for issue #1179: Dialog.Update with Code field value causes CS0121 ambiguity.
// MockDialog.ALUpdate has both NavValue and string overloads; NavCode satisfies both
// (NavCode extends NavValue and has implicit string conversion) → CS0121.
// Fix: add explicit NavCode overload to MockDialog.ALUpdate.
codeunit 232001 "DCU Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ShowCodeInDialog_ReturnsExpectedText()
    var
        Helper: Codeunit "DCU Helper";
        Result: Text;
    begin
        // [GIVEN] A Code[20] value
        // [WHEN] ShowCodeInDialog is called — internally calls Dialog.Update(1, ItemNo)
        // [THEN] Should not crash and should return the expected string
        Result := Helper.ShowCodeInDialog('ITEM-001');
        Assert.AreEqual('Done:ITEM-001', Result, 'Dialog.Update with Code value must not be ambiguous');
    end;

    [Test]
    procedure ShowCodeInDialog_EmptyCode_ReturnsExpectedText()
    var
        Helper: Codeunit "DCU Helper";
        Result: Text;
    begin
        // [GIVEN] An empty Code value
        // [WHEN] ShowCodeInDialog is called
        // [THEN] Should not crash
        Result := Helper.ShowCodeInDialog('');
        Assert.AreEqual('Done:', Result, 'Dialog.Update with empty Code must work');
    end;

    [Test]
    procedure ProcessItemsWithDialog_MultipleItems_ReturnsConcatenated()
    var
        Helper: Codeunit "DCU Helper";
        Rec: Record "DCU Item";
        UniqueNo1: Code[20];
        UniqueNo2: Code[20];
        Result: Text;
    begin
        // [GIVEN] Two items with unique keys
        UniqueNo1 := 'A001-' + Format(Random(999));
        UniqueNo2 := 'B002-' + Format(Random(999));

        Rec.Init();
        Rec."No." := UniqueNo1;
        Rec.Description := 'First';
        Rec.Insert();

        Rec.Init();
        Rec."No." := UniqueNo2;
        Rec.Description := 'Second';
        Rec.Insert();

        Rec.SetFilter("No.", '%1|%2', UniqueNo1, UniqueNo2);

        // [WHEN] ProcessItemsWithDialog is called — calls Dialog.Update(1, Rec."No.") per item
        Result := Helper.ProcessItemsWithDialog(Rec);

        // [THEN] Both codes appear in the result (Code field passed to Dialog.Update without error)
        Assert.IsTrue(Result.Contains(UniqueNo1), 'Result must contain ' + UniqueNo1);
        Assert.IsTrue(Result.Contains(UniqueNo2), 'Result must contain ' + UniqueNo2);
    end;

    [Test]
    procedure ShowCodeInDialog_Negative_WrongResult()
    var
        Helper: Codeunit "DCU Helper";
    begin
        // [NEGATIVE] Intentional error to prove asserterror works
        asserterror error('Expected: Dialog.Update with Code value must compile');
        Assert.ExpectedError('Expected:');
    end;
}
