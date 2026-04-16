codeunit 63401 "Test Record GetPosition"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetPosition_ReturnsNonEmptyString()
    var
        Rec: Record "Pos Test Table";
        Pos: Text;
    begin
        Rec.Init();
        Rec."No." := 'GP001';
        Rec.Insert();

        Rec.FindFirst();
        Pos := Rec.GetPosition();
        Assert.AreNotEqual('', Pos, 'GetPosition must return a non-empty string after FindFirst');
    end;

    [Test]
    procedure SetPosition_RestoresPrimaryKeyRow()
    var
        Rec: Record "Pos Test Table";
        Pos: Text;
        FirstNo: Code[20];
    begin
        // Insert two rows
        Rec.Init();
        Rec."No." := 'SP001';
        Rec.Insert();
        Rec.Init();
        Rec."No." := 'SP002';
        Rec.Insert();

        // Position at first row, save position
        Rec.FindFirst();
        FirstNo := Rec."No.";
        Pos := Rec.GetPosition();

        // Move to next row
        Rec.Next();
        Assert.AreNotEqual(FirstNo, Rec."No.", 'Next must advance to a different row');

        // Restore position — must be back on first row
        Rec.SetPosition(Pos);
        Assert.AreEqual(FirstNo, Rec."No.", 'SetPosition must restore the cursor to the saved row');
    end;

    [Test]
    procedure GetSetPosition_Roundtrip()
    var
        Rec: Record "Pos Test Table";
        Pos: Text;
        SavedNo: Code[20];
    begin
        Rec.Init();
        Rec."No." := 'RT001';
        Rec.Insert();
        Rec.Init();
        Rec."No." := 'RT002';
        Rec.Insert();
        Rec.Init();
        Rec."No." := 'RT003';
        Rec.Insert();

        // Move to last row, save position
        Rec.FindLast();
        SavedNo := Rec."No.";
        Pos := Rec.GetPosition();

        // Move to first row
        Rec.FindFirst();
        Assert.AreNotEqual(SavedNo, Rec."No.", 'FindFirst must give a different row than saved');

        // Restore
        Rec.SetPosition(Pos);
        Assert.AreEqual(SavedNo, Rec."No.", 'SetPosition roundtrip must return to the saved row');
    end;

    [Test]
    procedure SetPosition_InvalidString_RaisesError()
    var
        Rec: Record "Pos Test Table";
    begin
        Rec.Init();
        Rec."No." := 'INV001';
        Rec.Insert();

        asserterror Rec.SetPosition('totally-invalid-position-string');
        Assert.ExpectedError('');
    end;
}
