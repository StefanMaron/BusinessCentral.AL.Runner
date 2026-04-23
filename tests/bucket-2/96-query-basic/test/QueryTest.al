codeunit 229002 "Query Basic Test"
{
    Subtype = Test;

    local procedure ClearQueryItemTable()
    var
        QItem: Record "Query Item";
    begin
        QItem.DeleteAll();
    end;

    [Test]
    procedure OpenReadClose_SingleDataItem()
    var
        QItem: Record "Query Item";
        q: Query "Simple Item Query";
        RowCount: Integer;
    begin
        ClearQueryItemTable();
        QItem.Init();
        QItem."No." := 'ITEM001';
        QItem.Description := 'Widget';
        QItem."Unit Price" := 10.0;
        QItem.Quantity := 5;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'ITEM002';
        QItem.Description := 'Gadget';
        QItem."Unit Price" := 20.0;
        QItem.Quantity := 3;
        QItem.Insert();

        q.Open();
        while q.Read() do
            RowCount += 1;
        q.Close();

        Assert.AreEqual(2, RowCount, 'Expected 2 rows from query');
    end;

    [Test]
    procedure ColumnValues_ReturnedCorrectly()
    var
        QItem: Record "Query Item";
        q: Query "Simple Item Query";
    begin
        ClearQueryItemTable();
        QItem.Init();
        QItem."No." := 'X001';
        QItem.Description := 'TestWidget';
        QItem."Unit Price" := 42.50;
        QItem.Quantity := 7;
        QItem.Insert();

        q.Open();
        Assert.IsTrue(q.Read(), 'First Read should succeed');
        Assert.AreEqual('X001', q.ItemNo, 'ItemNo column should be X001');
        Assert.AreEqual('TestWidget', q.Description, 'Description column should be TestWidget');
        Assert.AreEqual(42.50, q.UnitPrice, 'UnitPrice column should be 42.50');
        Assert.AreEqual(7, q.Qty, 'Qty column should be 7');
        Assert.IsFalse(q.Read(), 'Second Read should return false');
        q.Close();
    end;

    [Test]
    procedure CloseAndReOpen_Works()
    var
        QItem: Record "Query Item";
        q: Query "Simple Item Query";
        Count1: Integer;
        Count2: Integer;
    begin
        ClearQueryItemTable();
        QItem.Init();
        QItem."No." := 'A001';
        QItem.Description := 'Alpha';
        QItem."Unit Price" := 1.0;
        QItem.Quantity := 1;
        QItem.Insert();

        q.Open();
        while q.Read() do
            Count1 += 1;
        q.Close();

        q.Open();
        while q.Read() do
            Count2 += 1;
        q.Close();

        Assert.AreEqual(1, Count1, 'First read should return 1 row');
        Assert.AreEqual(1, Count2, 'Second read after re-open should also return 1 row');
    end;

    [Test]
    procedure SetFilter_FiltersRows()
    var
        QItem: Record "Query Item";
        q: Query "Simple Item Query";
        RowCount: Integer;
    begin
        ClearQueryItemTable();
        QItem.Init();
        QItem."No." := 'F001';
        QItem.Description := 'Filter1';
        QItem."Unit Price" := 10.0;
        QItem.Quantity := 1;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'F002';
        QItem.Description := 'Filter2';
        QItem."Unit Price" := 20.0;
        QItem.Quantity := 2;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'F003';
        QItem.Description := 'Filter3';
        QItem."Unit Price" := 30.0;
        QItem.Quantity := 3;
        QItem.Insert();

        q.SetFilter(ItemNo, 'F002');
        q.Open();
        while q.Read() do
            RowCount += 1;
        q.Close();

        Assert.AreEqual(1, RowCount, 'Filter should return only 1 row');
    end;

    [Test]
    procedure SetRange_FiltersRows()
    var
        QItem: Record "Query Item";
        q: Query "Simple Item Query";
        RowCount: Integer;
    begin
        ClearQueryItemTable();
        QItem.Init();
        QItem."No." := 'R001';
        QItem.Description := 'Range1';
        QItem."Unit Price" := 10.0;
        QItem.Quantity := 1;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'R002';
        QItem.Description := 'Range2';
        QItem."Unit Price" := 20.0;
        QItem.Quantity := 2;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'R003';
        QItem.Description := 'Range3';
        QItem."Unit Price" := 30.0;
        QItem.Quantity := 3;
        QItem.Insert();

        q.SetRange(ItemNo, 'R001', 'R002');
        q.Open();
        while q.Read() do
            RowCount += 1;
        q.Close();

        Assert.AreEqual(2, RowCount, 'Range R001..R002 should return 2 rows');
    end;

    [Test]
    procedure TopNumberOfRows_LimitsResult()
    var
        QItem: Record "Query Item";
        q: Query "Simple Item Query";
        RowCount: Integer;
    begin
        ClearQueryItemTable();
        QItem.Init();
        QItem."No." := 'T001';
        QItem.Description := 'Top1';
        QItem."Unit Price" := 1.0;
        QItem.Quantity := 1;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'T002';
        QItem.Description := 'Top2';
        QItem."Unit Price" := 2.0;
        QItem.Quantity := 2;
        QItem.Insert();

        QItem.Init();
        QItem."No." := 'T003';
        QItem.Description := 'Top3';
        QItem."Unit Price" := 3.0;
        QItem.Quantity := 3;
        QItem.Insert();

        q.TopNumberOfRows(2);
        q.Open();
        while q.Read() do
            RowCount += 1;
        q.Close();

        Assert.AreEqual(2, RowCount, 'TopNumberOfRows(2) should return only 2 rows');
    end;

    [Test]
    procedure EmptyTable_ReturnsNoRows()
    var
        q: Query "Simple Item Query";
    begin
        ClearQueryItemTable();
        q.Open();
        Assert.IsFalse(q.Read(), 'Read on empty table should return false');
        q.Close();
    end;

    var
        Assert: Codeunit "Library Assert";
}
