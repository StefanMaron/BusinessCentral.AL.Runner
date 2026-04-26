codeunit 118002 "GTK Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── GoToKey ───────────────────────────────────────────────────────────────

    [Test]
    procedure GoToKey_ReturnsTrue_WhenRecordExists()
    var
        Rec: Record "GTK Table";
        TP: TestPage "GTK List";
    begin
        // Positive: GoToKey returns true when the record is in the table.
        Rec.Init();
        Rec."No." := 'K1';
        Rec.Name := 'Key One';
        Rec.Insert();

        TP.OpenView();
        Assert.IsTrue(TP.GoToKey('K1'), 'GoToKey must return true for an existing record');
        TP.Close();
    end;

    [Test]
    procedure GoToKey_ReturnsFalse_WhenRecordMissing()
    var
        TP: TestPage "GTK List";
    begin
        // Negative: GoToKey returns false when the key does not exist.
        TP.OpenView();
        Assert.IsFalse(TP.GoToKey('NOSUCHKEY'), 'GoToKey must return false for a missing record');
        TP.Close();
    end;

    [Test]
    procedure GoToKey_PositionsOnCorrectRecord()
    var
        Rec: Record "GTK Table";
        TP: TestPage "GTK List";
    begin
        // Positive: after GoToKey the page shows the correct record's field values.
        Rec.Init();
        Rec."No." := 'K2';
        Rec.Name := 'Positioned Name';
        Rec.Insert();

        TP.OpenView();
        TP.GoToKey('K2');
        Assert.AreEqual('Positioned Name', TP.Name.Value, 'GoToKey must position page on the correct record');
        TP.Close();
    end;

    // ── GoToRecord ────────────────────────────────────────────────────────────

    [Test]
    procedure GoToRecord_ReturnsTrue_WhenRecordExists()
    var
        Rec: Record "GTK Table";
        TP: TestPage "GTK List";
    begin
        // Positive: GoToRecord returns true for an inserted record.
        Rec.Init();
        Rec."No." := 'R1';
        Rec.Name := 'Rec One';
        Rec.Insert();
        Rec.Get('R1');

        TP.OpenView();
        Assert.IsTrue(TP.GoToRecord(Rec), 'GoToRecord must return true for an existing record');
        TP.Close();
    end;

    [Test]
    procedure GoToRecord_PositionsOnCorrectRecord()
    var
        Rec: Record "GTK Table";
        TP: TestPage "GTK List";
    begin
        // Positive: after GoToRecord the page shows the correct record's field values.
        Rec.Init();
        Rec."No." := 'R2';
        Rec.Name := 'Rec Two Name';
        Rec.Insert();
        Rec.Get('R2');

        TP.OpenView();
        TP.GoToRecord(Rec);
        Assert.AreEqual('Rec Two Name', TP.Name.Value, 'GoToRecord must position page on the correct record');
        TP.Close();
    end;
}
