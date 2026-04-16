codeunit 56501 "IsEmpty Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // IsEmpty on a table with no records
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEmptyReturnsTrueOnEmptyTable()
    var
        Rec: Record "IE Test Table";
    begin
        // [GIVEN] Table has no records (clean test isolation)
        // [WHEN] IsEmpty called with no filters
        // [THEN] Returns true
        Assert.IsTrue(Rec.IsEmpty(), 'IsEmpty must return true on a table with no rows');
    end;

    // -----------------------------------------------------------------------
    // IsEmpty on a table that has records
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEmptyReturnsFalseWhenRecordsExist()
    var
        Rec: Record "IE Test Table";
    begin
        // [GIVEN] Table has one record
        InsertRow('A001', 1);

        // [WHEN] IsEmpty called with no filters
        // [THEN] Returns false
        Assert.IsFalse(Rec.IsEmpty(), 'IsEmpty must return false when rows exist');
    end;

    // -----------------------------------------------------------------------
    // IsEmpty respects filters that exclude all records
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEmptyReturnsTrueWhenFilterExcludesAll()
    var
        Rec: Record "IE Test Table";
    begin
        // [GIVEN] Table has records but filter matches none
        InsertRow('B001', 1);
        InsertRow('B002', 2);
        Rec.SetRange(Status, 99);

        // [WHEN] IsEmpty called with excluding filter
        // [THEN] Returns true
        Assert.IsTrue(Rec.IsEmpty(), 'IsEmpty must return true when filter matches no rows');
    end;

    // -----------------------------------------------------------------------
    // IsEmpty respects filters that match some records
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEmptyReturnsFalseWhenFilterMatchesSome()
    var
        Rec: Record "IE Test Table";
    begin
        // [GIVEN] Table has records, filter matches at least one
        InsertRow('C001', 1);
        InsertRow('C002', 2);
        InsertRow('C003', 1);
        Rec.SetRange(Status, 1);

        // [WHEN] IsEmpty called with partial filter
        // [THEN] Returns false (2 rows match)
        Assert.IsFalse(Rec.IsEmpty(), 'IsEmpty must return false when filter matches some rows');
    end;

    // -----------------------------------------------------------------------
    // IsEmpty after Reset clears filter — table again visible
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEmptyAfterResetReflectsAllRows()
    var
        Rec: Record "IE Test Table";
    begin
        // [GIVEN] Table has records; filter set to exclude all; then Reset
        InsertRow('D001', 1);
        Rec.SetRange(Status, 99);
        Assert.IsTrue(Rec.IsEmpty(), 'Pre-condition: filter must exclude all rows');

        // [WHEN] Reset removes the filter
        Rec.Reset();

        // [THEN] IsEmpty returns false (rows are now visible)
        Assert.IsFalse(Rec.IsEmpty(), 'IsEmpty must return false after Reset when rows exist');
    end;

    local procedure InsertRow(No: Code[20]; Status: Integer)
    var
        Rec: Record "IE Test Table";
    begin
        Rec.Init();
        Rec."No." := No;
        Rec.Status := Status;
        Rec.Insert();
    end;
}
