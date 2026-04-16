codeunit 66001 "Record Count Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure InsertRow(Id: Integer; Category: Code[10]; Amount: Decimal)
    var
        Rec: Record "Count Rows Test Table";
    begin
        Rec.Init();
        Rec.Id := Id;
        Rec.Category := Category;
        Rec.Amount := Amount;
        Rec.Insert();
    end;

    // --- Count ---

    [Test]
    procedure Count_EmptyTable_ReturnsZero()
    var
        Rec: Record "Count Rows Test Table";
    begin
        // Negative: empty table must return 0
        Assert.AreEqual(0, Rec.Count, 'Count() on empty table must be 0');
    end;

    [Test]
    procedure Count_AfterInsertOne_ReturnsOne()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        Assert.AreEqual(1, Rec.Count, 'Count() after one insert must be 1');
    end;

    [Test]
    procedure Count_AfterInsertThree_ReturnsThree()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        InsertRow(2, 'B', 20);
        InsertRow(3, 'A', 30);
        Assert.AreEqual(3, Rec.Count, 'Count() after three inserts must be 3');
    end;

    [Test]
    procedure Count_WithFilter_ReturnsFilteredCount()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        InsertRow(2, 'B', 20);
        InsertRow(3, 'A', 30);
        Rec.SetRange(Category, 'A');
        Assert.AreEqual(2, Rec.Count, 'Count() with Category=A filter must be 2');
    end;

    [Test]
    procedure Count_WithFilter_NoMatch_ReturnsZero()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        InsertRow(2, 'B', 20);
        Rec.SetRange(Category, 'Z');
        Assert.AreEqual(0, Rec.Count, 'Count() with non-matching filter must be 0');
    end;

    [Test]
    procedure Count_AfterResetFilter_ReturnsTotalCount()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        InsertRow(2, 'B', 20);
        InsertRow(3, 'A', 30);
        Rec.SetRange(Category, 'A');
        // Reset filters — count must go back to full table
        Rec.Reset();
        Assert.AreEqual(3, Rec.Count, 'Count() after Reset() must return total row count');
    end;

    // --- CountApprox ---

    [Test]
    procedure CountApprox_EmptyTable_ReturnsZero()
    var
        Rec: Record "Count Rows Test Table";
    begin
        Assert.AreEqual(0, Rec.CountApprox, 'CountApprox() on empty table must be 0');
    end;

    [Test]
    procedure CountApprox_MatchesCount_AfterInserts()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        InsertRow(2, 'B', 20);
        // In runner context CountApprox returns exact count
        Assert.AreEqual(Rec.Count, Rec.CountApprox,
            'CountApprox() must equal Count() in runner context');
    end;

    [Test]
    procedure CountApprox_WithFilter_MatchesCount()
    var
        Rec: Record "Count Rows Test Table";
    begin
        InsertRow(1, 'A', 10);
        InsertRow(2, 'B', 20);
        InsertRow(3, 'A', 30);
        Rec.SetRange(Category, 'A');
        Assert.AreEqual(Rec.Count, Rec.CountApprox,
            'CountApprox() must equal Count() when a filter is active');
    end;
}
