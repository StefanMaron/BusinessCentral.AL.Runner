codeunit 54500 "Test LockTable"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure LockTableDoesNotThrow()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: LockTable on an empty table compiles and runs without error.
        Rec.LockTable();
        Assert.AreEqual(0, Rec.Count, 'Table should still be empty after LockTable');
    end;

    [Test]
    procedure ModifyWorksAfterLockTable()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: subsequent Modify operates normally after LockTable.
        Rec.Init();
        Rec."No." := 'A';
        Rec.Name := 'Original';
        Rec.Amount := 10;
        Rec.Insert(true);

        Rec.Get('A');
        Rec.LockTable();
        Rec.Name := 'Updated';
        Rec.Amount := 25;
        Rec.Modify(true);

        Rec.Get('A');
        Assert.AreEqual('Updated', Rec.Name,
            'Name should be updated after LockTable + Modify');
        Assert.AreEqual(25, Rec.Amount,
            'Amount should be updated after LockTable + Modify');
    end;

    [Test]
    procedure InsertWorksAfterLockTable()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: Insert operates normally after LockTable on an empty table.
        Rec.LockTable();

        Rec.Init();
        Rec."No." := 'B';
        Rec.Name := 'Second';
        Rec.Amount := 7;
        Rec.Insert(true);

        Rec.Get('B');
        Assert.AreEqual('Second', Rec.Name,
            'Record B should be insertable after LockTable');
    end;

    [Test]
    procedure LockTableIsIdempotent()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: multiple LockTable calls in a row do not break subsequent operations.
        Rec.Init();
        Rec."No." := 'C';
        Rec.Insert(true);

        Rec.LockTable();
        Rec.LockTable();
        Rec.LockTable();

        Assert.AreEqual(1, Rec.Count,
            'Repeated LockTable calls must not alter the table state');
    end;

    [Test]
    procedure DeleteWorksAfterLockTable()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: Delete works after LockTable.
        Rec.Init();
        Rec."No." := 'D';
        Rec.Insert(true);

        Rec.LockTable();
        Rec.Get('D');
        Rec.Delete(true);

        Assert.AreEqual(0, Rec.Count,
            'Record should be deleted after LockTable + Delete');
    end;

    [Test]
    procedure LockTableWaitFalseIsNoOp()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: LockTable(Wait: Boolean) overload is a no-op — must not raise an error.
        Rec.LockTable(false);
        Assert.AreEqual(0, Rec.Count, 'LockTable(false) must not alter the table or throw');
    end;

    [Test]
    procedure LockTableWaitTrueIsNoOp()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: LockTable(true) is also a no-op.
        Rec.LockTable(true);
        Assert.AreEqual(0, Rec.Count, 'LockTable(true) must not alter the table or throw');
    end;

    [Test]
    procedure LockTableWaitFalseDoesNotBlockInsert()
    var
        Rec: Record "Lock Probe";
    begin
        // Positive: Insert still works after LockTable(false).
        Rec.LockTable(false);
        Rec.Init();
        Rec."No." := 'E';
        Rec.Name := 'WithWait';
        Rec.Amount := 99;
        Rec.Insert(true);

        Rec.Get('E');
        Assert.AreEqual('WithWait', Rec.Name,
            'Record inserted after LockTable(false) must be retrievable');
        Assert.AreEqual(99, Rec.Amount,
            'Amount must match after LockTable(false) + Insert');
    end;
}
