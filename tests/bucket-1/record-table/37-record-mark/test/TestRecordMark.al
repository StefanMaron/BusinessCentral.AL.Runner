codeunit 54000 "Test Record Mark"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure MarkedOnlyReturnsOnlyMarkedRecords()
    var
        Rec: Record "Record Mark Table";
        Count: Integer;
    begin
        // Positive: marking a subset and enabling MarkedOnly returns only marked rows.
        InsertRow('A', 'Alpha', 10);
        InsertRow('B', 'Beta', 20);
        InsertRow('C', 'Gamma', 30);
        InsertRow('D', 'Delta', 40);

        Rec.FindSet();
        repeat
            if (Rec."No." = 'A') or (Rec."No." = 'C') then
                Rec.Mark(true);
        until Rec.Next() = 0;

        Rec.MarkedOnly(true);
        Count := 0;
        if Rec.FindSet() then
            repeat
                Count += 1;
                Assert.IsTrue((Rec."No." = 'A') or (Rec."No." = 'C'),
                    'Only marked records A and C should be returned, got ' + Rec."No.");
            until Rec.Next() = 0;
        Assert.AreEqual(2, Count, 'MarkedOnly iteration should yield exactly 2 records');
    end;

    [Test]
    procedure MarkGetterReturnsCurrentState()
    var
        Rec: Record "Record Mark Table";
    begin
        // Positive: Mark() returns true after Mark(true), false after Mark(false).
        InsertRow('X', 'xray', 1);
        Rec.Get('X');

        Assert.IsFalse(Rec.Mark(), 'Mark() should be false before marking');
        Rec.Mark(true);
        Assert.IsTrue(Rec.Mark(), 'Mark() should be true after Mark(true)');
        Rec.Mark(false);
        Assert.IsFalse(Rec.Mark(), 'Mark() should be false after Mark(false)');
    end;

    [Test]
    procedure ClearMarksRemovesAllMarks()
    var
        Rec: Record "Record Mark Table";
        Count: Integer;
    begin
        // Positive: ClearMarks wipes all marks; MarkedOnly iteration then finds nothing.
        InsertRow('P', 'P', 1);
        InsertRow('Q', 'Q', 2);
        InsertRow('R', 'R', 3);

        Rec.FindSet();
        repeat
            Rec.Mark(true);
        until Rec.Next() = 0;

        Rec.ClearMarks();
        Rec.MarkedOnly(true);

        Count := 0;
        if Rec.FindSet() then
            repeat
                Count += 1;
            until Rec.Next() = 0;
        Assert.AreEqual(0, Count, 'No records should be returned after ClearMarks');
    end;

    [Test]
    procedure MarkedOnlyOffReturnsAllRecords()
    var
        Rec: Record "Record Mark Table";
        Count: Integer;
    begin
        // Negative/off-switch: MarkedOnly(false) after marking still returns every record.
        InsertRow('1', 'One', 1);
        InsertRow('2', 'Two', 2);
        InsertRow('3', 'Three', 3);

        Rec.Get('1');
        Rec.Mark(true);

        Rec.MarkedOnly(false);
        Count := 0;
        if Rec.FindSet() then
            repeat
                Count += 1;
            until Rec.Next() = 0;
        Assert.AreEqual(3, Count, 'MarkedOnly(false) should return all records');
    end;

    [Test]
    procedure MarkBoolInIfCondition_CompileAndRun()
    var
        Rec: Record "Record Mark Table";
        Hit: Boolean;
    begin
        // Regression #1492: CS0019 "Operator '&' cannot be applied to operands of type 'bool' and 'void'"
        // BC emits "CStmtHit(N) & rec.ALMark(true)" for "if Rec.Mark(true) then", which requires
        // ALMark(bool) to return bool (not void).
        // The return value after marking should be truthy (mark was just set to true).
        InsertRow('Z1', 'Zulu', 99);
        Rec.Get('Z1');

        Hit := false;
        if Rec.Mark(true) then
            Hit := true;
        Assert.IsTrue(Hit, 'Branch must be taken: Mark(true) used in if-condition');

        // Negative: Mark(false) must return false so the branch is not taken
        Hit := true;
        if Rec.Mark(false) then
            Hit := false;
        Assert.IsTrue(Hit, 'Branch must NOT be taken: Mark(false) used in if-condition');
    end;

    local procedure InsertRow("No.": Code[20]; Name: Text[50]; Amount: Decimal)
    var
        Rec: Record "Record Mark Table";
    begin
        Rec.Init();
        Rec."No." := "No.";
        Rec.Name := Name;
        Rec.Amount := Amount;
        Rec.Insert(true);
    end;
}
