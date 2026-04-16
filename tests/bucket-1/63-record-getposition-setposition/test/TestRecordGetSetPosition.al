codeunit 63401 "Test Record GetPosition"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetPosition_ReturnsNonEmptyString()
    var
        Customer: Record Customer;
        Pos: Text;
    begin
        Customer.Init();
        Customer."No." := 'GP001';
        Customer.Insert();

        Customer.FindFirst();
        Pos := Customer.GetPosition();
        Assert.AreNotEqual('', Pos, 'GetPosition must return a non-empty string after FindFirst');
    end;

    [Test]
    procedure SetPosition_RestoresPrimaryKeyRow()
    var
        Customer: Record Customer;
        Pos: Text;
        FirstNo: Code[20];
    begin
        // Insert two rows
        Customer.Init();
        Customer."No." := 'SP001';
        Customer.Insert();
        Customer.Init();
        Customer."No." := 'SP002';
        Customer.Insert();

        // Position at first row, save position
        Customer.FindFirst();
        FirstNo := Customer."No.";
        Pos := Customer.GetPosition();

        // Move to next row
        Customer.Next();
        Assert.AreNotEqual(FirstNo, Customer."No.", 'Next must advance to a different row');

        // Restore position — must be back on first row
        Customer.SetPosition(Pos);
        Assert.AreEqual(FirstNo, Customer."No.", 'SetPosition must restore the cursor to the saved row');
    end;

    [Test]
    procedure GetSetPosition_Roundtrip()
    var
        Customer: Record Customer;
        Pos: Text;
        SavedNo: Code[20];
    begin
        Customer.Init();
        Customer."No." := 'RT001';
        Customer.Insert();
        Customer.Init();
        Customer."No." := 'RT002';
        Customer.Insert();
        Customer.Init();
        Customer."No." := 'RT003';
        Customer.Insert();

        // Move to last row, save position
        Customer.FindLast();
        SavedNo := Customer."No.";
        Pos := Customer.GetPosition();

        // Move to first row
        Customer.FindFirst();
        Assert.AreNotEqual(SavedNo, Customer."No.", 'FindFirst must give a different row than saved');

        // Restore
        Customer.SetPosition(Pos);
        Assert.AreEqual(SavedNo, Customer."No.", 'SetPosition roundtrip must return to the saved row');
    end;

    [Test]
    procedure SetPosition_InvalidString_RaisesError()
    var
        Customer: Record Customer;
    begin
        Customer.Init();
        Customer."No." := 'INV001';
        Customer.Insert();

        asserterror Customer.SetPosition('totally-invalid-position-string');
        Assert.ExpectedError('');
    end;
}
