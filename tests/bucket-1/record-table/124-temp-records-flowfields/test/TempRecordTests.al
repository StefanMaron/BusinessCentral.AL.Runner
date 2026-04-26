codeunit 56241 "Temp Record Tests"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    [Test]
    procedure TempRecordIsTemporaryReturnsTrue()
    var
        TempItem: Record "Temp Test Item" temporary;
    begin
        Assert.IsTrue(TempItem.IsTemporary(), 'Temporary record should return IsTemporary = true');
    end;

    [Test]
    procedure TempRecordInsertIsolatedFromNonTemp()
    var
        TempItem: Record "Temp Test Item" temporary;
        Item: Record "Temp Test Item";
    begin
        TempItem.Init();
        TempItem.Id := 1;
        TempItem.Name := 'Temp Only';
        TempItem.Insert();

        Assert.IsFalse(Item.FindFirst(), 'Non-temp variable should NOT see temp record');
    end;

    [Test]
    procedure NonTempRecordInsertNotVisibleToTemp()
    var
        TempItem: Record "Temp Test Item" temporary;
        Item: Record "Temp Test Item";
    begin
        Item.Init();
        Item.Id := 1;
        Item.Name := 'Real';
        Item.Insert();

        Assert.IsFalse(TempItem.FindFirst(), 'Temp variable should NOT see non-temp record');
    end;

    [Test]
    procedure TempRecordCountIsIsolated()
    var
        TempItem: Record "Temp Test Item" temporary;
        Item: Record "Temp Test Item";
    begin
        Item.Init();
        Item.Id := 1;
        Item.Insert();

        TempItem.Init();
        TempItem.Id := 2;
        TempItem.Insert();
        TempItem.Init();
        TempItem.Id := 3;
        TempItem.Insert();

        Assert.AreEqual(1, Item.Count(), 'Non-temp should have 1 record');
        Assert.AreEqual(2, TempItem.Count(), 'Temp should have 2 records');
    end;

    [Test]
    procedure NonTempRecordIsTemporaryReturnsFalse()
    var
        Item: Record "Temp Test Item";
    begin
        Assert.IsFalse(Item.IsTemporary(), 'Non-temp record should return IsTemporary = false');
    end;

    [Test]
    procedure TempRecordDuplicateInsertThrowsError()
    var
        TempItem: Record "Temp Test Item" temporary;
    begin
        TempItem.Init();
        TempItem.Id := 99;
        TempItem.Name := 'First';
        TempItem.Insert();

        asserterror begin
            TempItem.Init();
            TempItem.Id := 99;
            TempItem.Name := 'Duplicate';
            TempItem.Insert();
        end;
        Assert.ExpectedError('already exists');
    end;
}
