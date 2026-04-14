codeunit 56401 "PRR Page Run Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPageRunWithRecordCompiles()
    var
        Caller: Codeunit "PRR Caller";
        Item: Record "PRR Item";
        Result: Integer;
    begin
        Item."No." := 'X1';
        Item.Insert();

        Result := Caller.ShowItem(Item);

        Assert.AreEqual(42, Result, 'Page.Run(PageId, Rec) should be a no-op and allow caller to return');
    end;

    [Test]
    procedure TestPageRunModalWithRecordCompiles()
    var
        Caller: Codeunit "PRR Caller";
        Item: Record "PRR Item";
        Result: Integer;
    begin
        Item."No." := 'X2';
        Item.Insert();

        Result := Caller.ShowItemCurrRec(Item);

        Assert.AreEqual(43, Result, 'Page.RunModal(PageId, Rec) must be a no-op and allow caller to return');
    end;
}
