codeunit 50808 "GetFilter Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure InsertEntry(EntryNo: Integer; Name: Text[100]; Amount: Decimal; Category: Code[20])
    var
        Rec: Record "Filter Probe";
    begin
        Rec.Init();
        Rec."Entry No." := EntryNo;
        Rec."Name" := Name;
        Rec."Amount" := Amount;
        Rec."Category" := Category;
        Rec.Insert(true);
    end;

    // -----------------------------------------------------------------------
    // GetFilter — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure GetFilterReturnsRangeEqualityExpression()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with SetRange on a single value
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetRange("Entry No.", 1);

        // [WHEN] GetFilter is called for the filtered field
        // [THEN] It should return the filter expression '1'
        Assert.AreEqual('1', Rec.GetFilter("Entry No."), 'GetFilter should return equality value');
    end;

    [Test]
    procedure GetFilterReturnsRangeExpression()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with SetRange from..to
        InsertEntry(1, 'Alice', 100, 'A');
        InsertEntry(5, 'Eve', 500, 'E');
        Rec.SetRange("Entry No.", 2, 4);

        // [WHEN] GetFilter is called for the range-filtered field
        // [THEN] It should return 'FROM..TO' format
        Assert.AreEqual('2..4', Rec.GetFilter("Entry No."), 'GetFilter should return range expression');
    end;

    [Test]
    procedure GetFilterReturnsSetFilterExpression()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with SetFilter expression
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetFilter("Category", 'A|B');

        // [WHEN] GetFilter is called for the expression-filtered field
        // [THEN] It should return the filter expression as-is
        Assert.AreEqual('A|B', Rec.GetFilter("Category"), 'GetFilter should return SetFilter expression');
    end;

    [Test]
    procedure GetFilterReturnsTextRangeEquality()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with SetRange on a text field
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetRange("Name", 'Alice');

        // [WHEN] GetFilter is called for the text-filtered field
        // [THEN] It should return the filter value
        Assert.AreEqual('Alice', Rec.GetFilter("Name"), 'GetFilter should return text equality value');
    end;

    // -----------------------------------------------------------------------
    // GetFilter — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure GetFilterReturnsEmptyWhenNoFilter()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with no filters
        InsertEntry(1, 'Alice', 100, 'A');

        // [WHEN] GetFilter is called on a field with no filter
        // [THEN] It should return empty string
        Assert.AreEqual('', Rec.GetFilter("Entry No."), 'GetFilter should return empty when no filter');
    end;

    [Test]
    procedure GetFilterReturnsEmptyAfterReset()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with a filter, then Reset
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetRange("Entry No.", 1);
        Rec.Reset();

        // [WHEN] GetFilter is called after Reset
        // [THEN] It should return empty string
        Assert.AreEqual('', Rec.GetFilter("Entry No."), 'GetFilter should return empty after Reset');
    end;

    // -----------------------------------------------------------------------
    // GetFilters — all filters as a combined string
    // -----------------------------------------------------------------------

    [Test]
    procedure GetFiltersReturnsCombinedFilterString()
    var
        Rec: Record "Filter Probe";
        FilterText: Text;
    begin
        // [GIVEN] A record with multiple filters
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetRange("Entry No.", 1, 5);
        Rec.SetFilter("Category", 'A|B');

        // [WHEN] GetFilters is called
        FilterText := Rec.GetFilters();

        // [THEN] The result should contain both filter expressions
        Assert.AreNotEqual('', FilterText, 'GetFilters should return non-empty when filters active');
    end;

    [Test]
    procedure GetFiltersReturnsEmptyWhenNoFilters()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with no filters
        InsertEntry(1, 'Alice', 100, 'A');

        // [WHEN] GetFilters is called
        // [THEN] It should return empty string
        Assert.AreEqual('', Rec.GetFilters(), 'GetFilters should return empty when no filters');
    end;

    // -----------------------------------------------------------------------
    // HasFilter — boolean check for active filters
    // -----------------------------------------------------------------------

    [Test]
    procedure HasFilterReturnsTrueWhenFiltered()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with an active filter
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetRange("Entry No.", 1);

        // [WHEN/THEN] HasFilter should return true
        Assert.IsTrue(Rec.HasFilter(), 'HasFilter should be true when filter is active');
    end;

    [Test]
    procedure HasFilterReturnsFalseWhenNoFilter()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record with no filters
        InsertEntry(1, 'Alice', 100, 'A');

        // [WHEN/THEN] HasFilter should return false
        Assert.IsFalse(Rec.HasFilter(), 'HasFilter should be false when no filter');
    end;

    [Test]
    procedure HasFilterReturnsFalseAfterReset()
    var
        Rec: Record "Filter Probe";
    begin
        // [GIVEN] A record filtered, then Reset
        InsertEntry(1, 'Alice', 100, 'A');
        Rec.SetRange("Entry No.", 1);
        Rec.Reset();

        // [WHEN/THEN] HasFilter should return false after Reset
        Assert.IsFalse(Rec.HasFilter(), 'HasFilter should be false after Reset');
    end;
}
