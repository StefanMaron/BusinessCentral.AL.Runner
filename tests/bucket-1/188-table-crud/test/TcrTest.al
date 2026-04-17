/// Tests for Table CRUD operations — issue #685.
/// Covers: Insert, Get, Modify, Delete, Find, Reset, SetFilter, SetRange.
codeunit 124001 "TCR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure InsertItem(No: Code[20]; ItemName: Text[50]; Qty: Integer; Cat: Code[10])
    var
        Item: Record "TCR Item";
    begin
        Item.Init();
        Item."No." := No;
        Item.Name := ItemName;
        Item.Quantity := Qty;
        Item.Category := Cat;
        Item.Insert();
    end;

    // ── Insert / Get ──────────────────────────────────────────────────────────

    [Test]
    procedure Insert_Get_RoundTrips()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('A001', 'Apple', 10, 'FRUIT');
        Assert.IsTrue(Item.Get('A001'), 'Get must return true for inserted record');
        Assert.AreEqual('Apple', Item.Name, 'Name must match inserted value');
        Assert.AreEqual(10, Item.Quantity, 'Quantity must match inserted value');
    end;

    [Test]
    procedure Get_Missing_ReturnsFalse()
    var
        Item: Record "TCR Item";
    begin
        Assert.IsFalse(Item.Get('MISSING'), 'Get must return false for non-existent record');
    end;

    // ── Modify ────────────────────────────────────────────────────────────────

    [Test]
    procedure Modify_ChangesValue()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('B001', 'Banana', 5, 'FRUIT');
        Item.Get('B001');
        Item.Quantity := 99;
        Item.Modify();
        Item.Get('B001');
        Assert.AreEqual(99, Item.Quantity, 'Quantity must reflect Modify value');
    end;

    [Test]
    procedure Modify_DoesNotAffectOtherRecords()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('C001', 'Cherry', 20, 'FRUIT');
        InsertItem('C002', 'Cranberry', 30, 'FRUIT');
        Item.Get('C001');
        Item.Quantity := 99;
        Item.Modify();
        Item.Get('C002');
        Assert.AreEqual(30, Item.Quantity, 'Other record must not be affected by Modify');
    end;

    // ── Delete ────────────────────────────────────────────────────────────────

    [Test]
    procedure Delete_RemovesRecord()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('D001', 'Date', 15, 'FRUIT');
        Assert.IsTrue(Item.Get('D001'), 'Record must exist before Delete');
        Item.Delete();
        Assert.IsFalse(Item.Get('D001'), 'Get must return false after Delete');
    end;

    [Test]
    procedure Delete_DoesNotAffectOtherRecords()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('E001', 'Elderberry', 7, 'BERRY');
        InsertItem('E002', 'Eggplant', 3, 'VEG');
        Item.Get('E001');
        Item.Delete();
        Assert.IsTrue(Item.Get('E002'), 'Other record must still exist after Delete');
    end;

    // ── Find ──────────────────────────────────────────────────────────────────

    [Test]
    procedure Find_First_PositionsOnFirstRecord()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('F001', 'Fig', 8, 'FRUIT');
        InsertItem('F002', 'Feijoa', 4, 'FRUIT');
        Assert.IsTrue(Item.Find('-'), 'Find(-) must return true when records exist');
        Assert.AreEqual('F001', Item."No.", 'Find(-) must position on first record');
    end;

    [Test]
    procedure Find_EmptyTable_ReturnsFalse()
    var
        Item: Record "TCR Item";
    begin
        Assert.IsFalse(Item.Find('-'), 'Find(-) must return false on empty table');
    end;

    // ── SetRange / Reset ──────────────────────────────────────────────────────

    [Test]
    procedure SetRange_FiltersRecords()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('G001', 'Grape', 10, 'FRUIT');
        InsertItem('G002', 'Garlic', 5, 'VEG');
        InsertItem('G003', 'Ginger', 3, 'VEG');
        Item.SetRange(Category, 'VEG');
        Assert.AreEqual(2, Item.Count(), 'SetRange must filter to 2 VEG records');
    end;

    [Test]
    procedure Reset_ClearsFilter()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('H001', 'Hazelnut', 12, 'NUT');
        InsertItem('H002', 'Honey', 6, 'OTHER');
        Item.SetRange(Category, 'NUT');
        Assert.AreEqual(1, Item.Count(), 'Before Reset must be 1');
        Item.Reset();
        Assert.AreEqual(2, Item.Count(), 'After Reset must be 2 (filter cleared)');
    end;

    // ── SetFilter ─────────────────────────────────────────────────────────────

    [Test]
    procedure SetFilter_WildcardFiltersRecords()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('I001', 'Ice Apple', 9, 'FRUIT');
        InsertItem('I002', 'Iceberry', 4, 'BERRY');
        InsertItem('I003', 'Mango', 20, 'FRUIT');
        Item.SetFilter(Name, 'Ice*');
        Assert.AreEqual(2, Item.Count(), 'SetFilter Ice* must match 2 records');
    end;

    [Test]
    procedure SetFilter_NoMatch_CountIsZero()
    var
        Item: Record "TCR Item";
    begin
        InsertItem('J001', 'Jackfruit', 11, 'FRUIT');
        Item.SetFilter(Name, 'ZZZNOTFOUND*');
        Assert.AreEqual(0, Item.Count(), 'SetFilter with no match must return Count = 0');
    end;
}
