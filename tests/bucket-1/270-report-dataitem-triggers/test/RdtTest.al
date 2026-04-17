codeunit 84503 "RDT Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── OnAfterGetRecord fires for each record ──────────────────────────────────
    [Test]
    procedure OnAfterGetRecord_FiresForEachRecord()
    var
        Rec: Record "RDT Record";
        Rep: Report "RDT Report";
    begin
        // Positive: OnAfterGetRecord must fire once per record in the dataitem table.
        Rec.DeleteAll();
        Rec.Id := 1; Rec.Amount := 10; Rec.Insert();
        Rec.Id := 2; Rec.Amount := 20; Rec.Insert();

        Rep.UseRequestPage(false);
        Rep.Run();

        Assert.AreEqual(2, Rep.GetRecordCount(), 'OnAfterGetRecord must fire for each record');
    end;

    // ── TotalAmount accumulates across all records ──────────────────────────────
    [Test]
    procedure OnAfterGetRecord_AccumulatesValues()
    var
        Rec: Record "RDT Record";
        Rep: Report "RDT Report";
    begin
        // Positive: OnAfterGetRecord accumulates Amount across all rows.
        Rec.DeleteAll();
        Rec.Id := 1; Rec.Amount := 10; Rec.Insert();
        Rec.Id := 2; Rec.Amount := 20; Rec.Insert();

        Rep.UseRequestPage(false);
        Rep.Run();

        Assert.AreEqual(30, Rep.GetTotalAmount(), 'TotalAmount should sum all records');
    end;

    // ── Empty table: OnAfterGetRecord never fires ───────────────────────────────
    [Test]
    procedure OnAfterGetRecord_ZeroRecords_CountIsZero()
    var
        Rec: Record "RDT Record";
        Rep: Report "RDT Report";
    begin
        // Negative: when the table is empty, RecordCount stays 0.
        Rec.DeleteAll();

        Rep.UseRequestPage(false);
        Rep.Run();

        Assert.AreEqual(0, Rep.GetRecordCount(), 'RecordCount must be 0 when table is empty');
    end;

    // ── OnPreDataItem fires exactly once ────────────────────────────────────────
    [Test]
    procedure OnPreDataItem_FiresOnce()
    var
        Rec: Record "RDT Record";
        Rep: Report "RDT Report";
    begin
        // Positive: OnPreDataItem fires exactly once per dataitem.
        Rec.DeleteAll();
        Rec.Id := 1; Rec.Amount := 5; Rec.Insert();

        Rep.UseRequestPage(false);
        Rep.Run();

        Assert.AreEqual(1, Rep.GetPreDataItemCount(), 'OnPreDataItem must fire exactly once');
    end;

    // ── OnPostDataItem fires exactly once ───────────────────────────────────────
    [Test]
    procedure OnPostDataItem_FiresOnce()
    var
        Rec: Record "RDT Record";
        Rep: Report "RDT Report";
    begin
        // Positive: OnPostDataItem fires exactly once per dataitem.
        Rec.DeleteAll();
        Rec.Id := 1; Rec.Amount := 5; Rec.Insert();

        Rep.UseRequestPage(false);
        Rep.Run();

        Assert.AreEqual(1, Rep.GetPostDataItemCount(), 'OnPostDataItem must fire exactly once');
    end;

    // ── Single record: count is exactly 1 ──────────────────────────────────────
    [Test]
    procedure OnAfterGetRecord_SingleRecord_CountIsOne()
    var
        Rec: Record "RDT Record";
        Rep: Report "RDT Report";
    begin
        // Positive: one record → RecordCount = 1.
        Rec.DeleteAll();
        Rec.Id := 1; Rec.Amount := 42; Rec.Insert();

        Rep.UseRequestPage(false);
        Rep.Run();

        Assert.AreEqual(1, Rep.GetRecordCount(), 'RecordCount must be 1 for single record');
        Assert.AreEqual(42, Rep.GetTotalAmount(), 'TotalAmount must equal the single record Amount');
    end;
}
