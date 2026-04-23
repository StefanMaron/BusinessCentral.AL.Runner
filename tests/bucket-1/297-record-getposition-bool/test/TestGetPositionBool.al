codeunit 297003 "Test GetPosition Boolean"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetPosition_True_UsesFieldNames()
    var
        Rec: Record "GetPos Bool Test Table";
        Pos: Text;
    begin
        // GetPosition(true) must return a string with the field name "No." and the PK value.
        // Insert then Get to ensure the record is positioned on this exact row regardless
        // of any pre-existing rows from other tests sharing the table (codeunit isolation).
        Rec.Init();
        Rec."No." := 'GBT001';
        Rec.Insert();
        Rec.Get('GBT001');

        Pos := Rec.GetPosition(true);
        // Pos must contain the field name "No." (not a field number)
        Assert.IsTrue(StrPos(Pos, 'No.') > 0, 'GetPosition(true) must contain field name No. in the position string');
        // Pos must contain the actual PK value
        Assert.AreEqual('No.=CONST(GBT001)', Pos, 'GetPosition(true) must use field name and include PK value');
    end;

    [Test]
    procedure GetPosition_False_UsesFieldNumbers()
    var
        Rec: Record "GetPos Bool Test Table";
        Pos: Text;
    begin
        // GetPosition(false) must use field number 1 (the PK "No." is field 1) instead of field name.
        // Insert then Get to ensure we are positioned on this exact row.
        Rec.Init();
        Rec."No." := 'GBF001';
        Rec.Insert();
        Rec.Get('GBF001');

        Pos := Rec.GetPosition(false);
        // Pos must NOT contain the field name — must use field number "1"
        Assert.AreEqual('1=CONST(GBF001)', Pos, 'GetPosition(false) must use field number and include PK value');
    end;

    [Test]
    procedure GetPosition_True_And_False_DifferFromEachOther()
    var
        Rec: Record "GetPos Bool Test Table";
        PosWithNames: Text;
        PosWithNumbers: Text;
    begin
        // The two overloads must produce different strings (field name vs field number).
        Rec.Init();
        Rec."No." := 'DIFF001';
        Rec.Insert();
        Rec.Get('DIFF001');

        PosWithNames := Rec.GetPosition(true);
        PosWithNumbers := Rec.GetPosition(false);
        Assert.AreNotEqual(PosWithNames, PosWithNumbers,
            'GetPosition(true) and GetPosition(false) must differ: one uses names, the other field numbers');
    end;
}
