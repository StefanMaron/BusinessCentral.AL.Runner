codeunit 55601 "Test Delete All"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure InsertRow(Id: Integer; Status: Integer; Name: Text[50])
    var
        Rec: Record "Delete All Table";
    begin
        Rec.Id := Id;
        Rec.Status := Status;
        Rec.Name := Name;
        Rec.Insert();
    end;

    local procedure CountAll(): Integer
    var
        Rec: Record "Delete All Table";
    begin
        exit(Rec.Count());
    end;

    [Test]
    procedure DeleteAll_NoFilters_DeletesAllRecords()
    var
        Rec: Record "Delete All Table";
    begin
        // Setup: 3 records
        InsertRow(1, 1, 'Alpha');
        InsertRow(2, 2, 'Beta');
        InsertRow(3, 1, 'Gamma');

        Rec.DeleteAll();

        Assert.AreEqual(0, CountAll(), 'DeleteAll with no filters must remove all records');
    end;

    [Test]
    procedure DeleteAll_WithSetRange_DeletesOnlyMatchingRecords()
    var
        Rec: Record "Delete All Table";
    begin
        // Setup: Status=1 (2 rows) and Status=2 (1 row)
        InsertRow(1, 1, 'Alpha');
        InsertRow(2, 2, 'Beta');
        InsertRow(3, 1, 'Gamma');

        Rec.SetRange(Status, 1);
        Rec.DeleteAll();

        // Only Status=2 row remains
        Assert.AreEqual(1, CountAll(), 'DeleteAll(SetRange) must leave non-matching records intact');
        Rec.Reset();
        Rec.SetRange(Status, 2);
        Assert.AreEqual(1, Rec.Count(), 'Status=2 record must survive DeleteAll(Status=1)');
    end;

    [Test]
    procedure DeleteAll_WithSetFilter_DeletesOnlyMatchingRecords()
    var
        Rec: Record "Delete All Table";
    begin
        // Setup: Id 1..5
        InsertRow(1, 0, 'One');
        InsertRow(2, 0, 'Two');
        InsertRow(3, 0, 'Three');
        InsertRow(4, 0, 'Four');
        InsertRow(5, 0, 'Five');

        Rec.SetFilter(Id, '>%1', 3);
        Rec.DeleteAll();

        // Id 4 and 5 deleted; Id 1,2,3 remain
        Assert.AreEqual(3, CountAll(), 'DeleteAll(SetFilter >3) must delete exactly 2 records');
    end;

    [Test]
    procedure DeleteAll_EmptyTable_DoesNotError()
    var
        Rec: Record "Delete All Table";
    begin
        // Table starts empty — should not raise any error
        Rec.DeleteAll();
        Assert.AreEqual(0, CountAll(), 'DeleteAll on empty table must leave count at 0');
    end;

    [Test]
    procedure DeleteAll_EmptyTableWithFilter_DoesNotError()
    var
        Rec: Record "Delete All Table";
    begin
        Rec.SetRange(Status, 99);
        Rec.DeleteAll();
        Assert.AreEqual(0, CountAll(), 'DeleteAll with non-matching filter on empty table must not error');
    end;

    [Test]
    procedure DeleteAll_CountAfterPartialDelete_IsCorrect()
    var
        Rec: Record "Delete All Table";
    begin
        InsertRow(1, 10, 'A');
        InsertRow(2, 10, 'B');
        InsertRow(3, 20, 'C');
        InsertRow(4, 20, 'D');
        InsertRow(5, 30, 'E');

        Rec.SetRange(Status, 20);
        Rec.DeleteAll();

        Assert.AreEqual(3, CountAll(), 'Three records (Status 10x2, 30x1) must remain after deleting Status=20');
    end;
}
