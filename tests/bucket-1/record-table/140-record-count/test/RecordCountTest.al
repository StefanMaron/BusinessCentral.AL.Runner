codeunit 50141 "CNT Record Count Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "CNT Helper";

    [Test]
    procedure Count_EmptyTable_ReturnsZero()
    begin
        // Positive: Count on empty table returns 0, not throws.
        Assert.AreEqual(0, Helper.GetCount(), 'Count on empty table must be 0');
    end;

    [Test]
    procedure Count_AfterInsert_ReturnsCorrectTotal()
    begin
        // Positive: Count returns the number of inserted records.
        Helper.InsertItems(5);
        Assert.AreEqual(5, Helper.GetCount(), 'Count must return 5 after inserting 5 records');
    end;

    [Test]
    procedure Count_NotZeroAfterInsert()
    begin
        // Negative: Count must NOT return 0 when records exist.
        Helper.InsertItems(3);
        Assert.AreNotEqual(0, Helper.GetCount(), 'Count must not be 0 when records exist');
    end;

    [Test]
    procedure Count_WithFilter_ReturnsFilteredCount()
    begin
        // Positive: Count with SetRange returns only matching records.
        // InsertItems(6): items 1,3,5 are Active=true → count = 3.
        Helper.InsertItems(6);
        Assert.AreEqual(3, Helper.GetActiveCount(), 'Active count must be 3 out of 6');
    end;

    [Test]
    procedure Count_WithFilter_NotTotalCount()
    begin
        // Negative: filtered Count must NOT equal the total count.
        Helper.InsertItems(4);
        // InsertItems(4): items 1,3 are Active=true → active count = 2, total = 4.
        Assert.AreNotEqual(Helper.GetCount(), Helper.GetActiveCount(),
            'Filtered count must differ from total count');
    end;

    [Test]
    procedure Count_WithCategoryFilter_ReturnsCorrect()
    begin
        // Positive: SetRange on Category field returns correct subset count.
        // InsertItems(6): items 3,6 have Category='C' → count = 2.
        Helper.InsertItems(6);
        Assert.AreEqual(2, Helper.GetCountByCategory('C'),
            'Category C count must be 2 out of 6 items');
        Assert.AreEqual(4, Helper.GetCountByCategory('A'),
            'Category A count must be 4 out of 6 items');
    end;

    [Test]
    procedure Count_WithNoMatchingFilter_ReturnsZero()
    begin
        // Positive: SetRange with no matching records returns 0.
        Helper.InsertItems(3);
        Assert.AreEqual(0, Helper.GetCountByCategory('Z'),
            'Count with no matches must return 0');
    end;

    [Test]
    procedure IsEmpty_EmptyTable_ReturnsTrue()
    begin
        // Positive: IsEmpty on empty table returns true.
        Assert.IsTrue(Helper.IsEmptyTable(), 'IsEmpty must be true on empty table');
    end;

    [Test]
    procedure IsEmpty_AfterInsert_ReturnsFalse()
    begin
        // Positive: IsEmpty returns false after inserting records.
        Helper.InsertItems(1);
        Assert.IsFalse(Helper.IsEmptyTable(), 'IsEmpty must be false after insert');
    end;
}
