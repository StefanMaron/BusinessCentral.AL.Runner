// In-memory multi-dataitem query-join reproducer.
//
// Seed data (deterministic):
//   QJ Customer:  C1 "Alice", C2 "Bob", C3 "Carol"
//   QJ Order:     1 -> C1 amount 100
//                 2 -> C1 amount 200
//                 3 -> C2 amount 300
//   (C3 "Carol" has NO order row → exercises InnerJoin drop vs LeftOuterJoin keep.)
//
// Expected InnerJoin result (OrderBy EntryNo): three rows, Carol absent.
//   (C1,Alice, 1, 100), (C1,Alice, 2, 200), (C2,Bob, 3, 300)
// Expected LeftOuterJoin result (OrderBy CustNo): four rows, Carol kept with
//   default child columns (EntryNo 0, Amount 0).
//   (C1,Alice,1,100),(C1,Alice,2,200),(C2,Bob,3,300),(C3,Carol,0,0)
codeunit 60391 "QJ Query Join Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "QJ Assert";

    local procedure Seed()
    var
        Cust: Record "QJ Customer";
        Ord: Record "QJ Order";
    begin
        Cust.DeleteAll();
        Ord.DeleteAll();

        InsertCust(Cust, 'C1', 'Alice');
        InsertCust(Cust, 'C2', 'Bob');
        InsertCust(Cust, 'C3', 'Carol');

        InsertOrder(Ord, 1, 'C1', 100);
        InsertOrder(Ord, 2, 'C1', 200);
        InsertOrder(Ord, 3, 'C2', 300);
    end;

    local procedure InsertCust(var Cust: Record "QJ Customer"; No: Code[20]; Name: Text[50])
    begin
        Cust.Init();
        Cust."No." := No;
        Cust.Name := Name;
        Cust.Insert();
    end;

    local procedure InsertOrder(var Ord: Record "QJ Order"; EntryNo: Integer; CustNo: Code[20]; Amount: Decimal)
    begin
        Ord.Init();
        Ord."Entry No." := EntryNo;
        Ord."Customer No." := CustNo;
        Ord.Amount := Amount;
        Ord.Insert();
    end;

    [Test]
    procedure InnerJoin_PairsParentWithChild_DropsUnmatchedParent()
    var
        Q: Query "QJ Cust Orders Inner";
        RowCount: Integer;
    begin
        Seed();
        Q.Open();
        RowCount := 0;

        // Row 1: order 1 → Alice / 100
        Assert.IsTrue(Q.Read(), 'InnerJoin must return the first joined row');
        RowCount += 1;
        Assert.AreEqual('C1', Q.CustNo, 'Row1 customer no');
        Assert.AreEqual('Alice', Q.CustName, 'Row1 customer name paired with its order');
        Assert.AreEqual(1, Q.EntryNo, 'Row1 order entry no');
        Assert.AreEqual(100, Q.Amount, 'Row1 order amount belongs to the linked customer');

        // Row 2: order 2 → Alice / 200
        Assert.IsTrue(Q.Read(), 'InnerJoin must return the second joined row');
        RowCount += 1;
        Assert.AreEqual('C1', Q.CustNo, 'Row2 customer no');
        Assert.AreEqual('Alice', Q.CustName, 'Row2 customer name');
        Assert.AreEqual(2, Q.EntryNo, 'Row2 order entry no');
        Assert.AreEqual(200, Q.Amount, 'Row2 order amount');

        // Row 3: order 3 → Bob / 300
        Assert.IsTrue(Q.Read(), 'InnerJoin must return the third joined row');
        RowCount += 1;
        Assert.AreEqual('C2', Q.CustNo, 'Row3 customer no');
        Assert.AreEqual('Bob', Q.CustName, 'Row3 customer name');
        Assert.AreEqual(3, Q.EntryNo, 'Row3 order entry no');
        Assert.AreEqual(300, Q.Amount, 'Row3 order amount');

        // Carol (C3) has no order → InnerJoin drops her.
        Assert.IsFalse(Q.Read(), 'InnerJoin must NOT emit the unmatched parent (Carol)');
        Q.Close();
        Assert.AreEqual(3, RowCount, 'InnerJoin must return exactly 3 joined rows');
    end;

    [Test]
    procedure LeftOuterJoin_KeepsUnmatchedParent_WithDefaultChildColumns()
    var
        Q: Query "QJ Cust Orders Left";
        RowCount: Integer;
        SawCarol: Boolean;
    begin
        Seed();
        Q.Open();
        RowCount := 0;
        SawCarol := false;

        while Q.Read() do begin
            RowCount += 1;
            if Q.CustNo = 'C3' then begin
                SawCarol := true;
                Assert.AreEqual('Carol', Q.CustName, 'Unmatched parent name is preserved');
                Assert.AreEqual(0, Q.EntryNo, 'LeftOuterJoin leaves child entry no at default for unmatched parent');
                Assert.AreEqual(0, Q.Amount, 'LeftOuterJoin leaves child amount at default for unmatched parent');
            end;
        end;
        Q.Close();

        // Three matched order rows + one kept-but-unmatched Carol = 4.
        Assert.AreEqual(4, RowCount, 'LeftOuterJoin keeps the unmatched parent → 4 rows');
        Assert.IsTrue(SawCarol, 'LeftOuterJoin must emit the unmatched parent (Carol)');
    end;

    [Test]
    procedure RightOuterJoin_IsOutOfScope_ThrowsNamedReason()
    var
        Q: Query "QJ Cust Orders Right";
    begin
        // RightOuterJoin is not faithfully reproducible by the in-memory nested-loop join.
        // Per loud-failures, opening/reading it must throw RunnerOutOfScopeException naming
        // the API and a specific reason — never silently return wrong rows.
        Seed();
        asserterror begin
            Q.Open();
            Q.Read();
        end;
        Assert.ExpectedError('query-join-rightouterjoin-not-implemented');
    end;

    [Test]
    procedure InnerJoin_NoChildRows_ClosesResultSet()
    var
        Cust: Record "QJ Customer";
        Ord: Record "QJ Order";
        Q: Query "QJ Cust Orders Inner";
    begin
        // Parent rows exist but NO child rows → InnerJoin yields zero rows.
        Cust.DeleteAll();
        Ord.DeleteAll();
        InsertCust(Cust, 'C1', 'Alice');
        InsertCust(Cust, 'C2', 'Bob');

        Q.Open();
        Assert.IsFalse(Q.Read(), 'InnerJoin with no child rows must return an empty resultset');
        Q.Close();
    end;
}
