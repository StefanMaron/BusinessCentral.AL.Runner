codeunit 50809 "CurrentKey Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // CurrentKey — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CurrentKeyReturnsKeyAfterSetCurrentKey()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] A record with SetCurrentKey("Name")
        Rec.SetCurrentKey("Name");

        // [WHEN/THEN] CurrentKey returns the field name — not just non-empty
        Assert.AreEqual('Name', Rec.CurrentKey(), 'CurrentKey must return "Name" after SetCurrentKey(Name)');
    end;

    // -----------------------------------------------------------------------
    // CurrentKey — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CurrentKeyReturnsDefaultWhenNotSet()
    var
        Rec: Record "Key Probe";
        KeyText: Text;
    begin
        // [GIVEN] A record with no explicit SetCurrentKey
        // [WHEN] CurrentKey is read, [THEN] returns the PK field name (non-empty)
        KeyText := Rec.CurrentKey();
        Assert.AreNotEqual('', KeyText, 'CurrentKey must return PK field name by default, not empty');
    end;

    // -----------------------------------------------------------------------
    // Ascending — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure AscendingDefaultsToTrue()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] A record with default sort order
        Rec.SetCurrentKey("Name");

        // [WHEN/THEN] Ascending should default to true
        Assert.IsTrue(Rec.Ascending(), 'Ascending should default to true');
    end;

    [Test]
    procedure AscendingReturnsFalseAfterSetAscendingFalse()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] A record with descending sort
        Rec.SetCurrentKey("Name");
        Rec.SetAscending("Name", false);

        // [WHEN/THEN] Ascending should return false
        Assert.IsFalse(Rec.Ascending(), 'Ascending should return false after SetAscending(false)');
    end;

    // -----------------------------------------------------------------------
    // SetCurrentKey traversal — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure SetCurrentKeyByNameChangesTraversalOrder()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] Records inserted in PK (Code) order: AAA/Zoe, BBB/Anna, CCC/Mike
        InsertKeyProbe('AAA', 'Zoe', 3);
        InsertKeyProbe('BBB', 'Anna', 1);
        InsertKeyProbe('CCC', 'Mike', 2);

        // [WHEN] SetCurrentKey by Name and FindSet
        Rec.SetCurrentKey("Name");
        Rec.FindSet();

        // [THEN] First record is Anna (alphabetically first), not Zoe (PK-first)
        Assert.AreEqual('Anna', Rec."Name", 'First record by Name should be Anna');
        Rec.Next();
        Assert.AreEqual('Mike', Rec."Name", 'Second record by Name should be Mike');
        Rec.Next();
        Assert.AreEqual('Zoe', Rec."Name", 'Third record by Name should be Zoe');
    end;

    [Test]
    procedure SetCurrentKeyBySequenceChangesTraversalOrder()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] Records inserted in PK order: P1/Seq=30, P2/Seq=10, P3/Seq=20
        InsertKeyProbe('P1', 'Beta', 30);
        InsertKeyProbe('P2', 'Alpha', 10);
        InsertKeyProbe('P3', 'Gamma', 20);

        // [WHEN] SetCurrentKey by Sequence ascending and FindSet
        Rec.SetCurrentKey("Sequence");
        Rec.SetAscending("Sequence", true);
        Rec.FindSet();

        // [THEN] Records traverse in Sequence order: 10, 20, 30
        Assert.AreEqual(10, Rec."Sequence", 'First by Sequence should be 10');
        Rec.Next();
        Assert.AreEqual(20, Rec."Sequence", 'Second by Sequence should be 20');
        Rec.Next();
        Assert.AreEqual(30, Rec."Sequence", 'Third by Sequence should be 30');
    end;

    [Test]
    procedure ResettingToPrimaryKeyRestoresPKOrder()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] Records inserted out of PK order intent: CCC, AAA, BBB
        InsertKeyProbe('CCC', 'Zara', 3);
        InsertKeyProbe('AAA', 'Anna', 1);
        InsertKeyProbe('BBB', 'Mike', 2);

        // [WHEN] First sort by Name (changes order), then reset to PK (Code)
        Rec.SetCurrentKey("Name");
        Rec.SetCurrentKey("Code");
        Rec.FindSet();

        // [THEN] Traversal is in PK (Code) order: AAA, BBB, CCC
        Assert.AreEqual('AAA', Rec."Code", 'First by Code PK should be AAA');
        Rec.Next();
        Assert.AreEqual('BBB', Rec."Code", 'Second by Code PK should be BBB');
        Rec.Next();
        Assert.AreEqual('CCC', Rec."Code", 'Third by Code PK should be CCC');
    end;

    // -----------------------------------------------------------------------
    // SetCurrentKey traversal — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure SetCurrentKeyByNameDoesNotTraverseInPKOrder()
    var
        Rec: Record "Key Probe";
        FirstCode: Code[20];
    begin
        // [GIVEN] Records where Name-order differs from PK order: X1/Bob, X2/Alice
        InsertKeyProbe('X1', 'Bob', 2);
        InsertKeyProbe('X2', 'Alice', 1);

        // [WHEN] SetCurrentKey by Name and read the first record
        Rec.SetCurrentKey("Name");
        Rec.FindFirst();
        FirstCode := Rec."Code";

        // [THEN] First record by Name is X2 (Alice), NOT X1 (PK-first order)
        Assert.AreEqual('X2', FirstCode, 'Name sort should return X2 (Alice) first, not X1 (Bob)');
        Assert.AreNotEqual('X1', FirstCode, 'PK-order record X1 should NOT be first when sorted by Name');
    end;

    [Test]
    procedure SetCurrentKeyDescendingReversesOrder()
    var
        Rec: Record "Key Probe";
    begin
        // [GIVEN] Records: D1/Seq=5, D2/Seq=15, D3/Seq=10
        InsertKeyProbe('D1', 'Alpha', 5);
        InsertKeyProbe('D2', 'Beta', 15);
        InsertKeyProbe('D3', 'Gamma', 10);

        // [WHEN] SetCurrentKey by Sequence descending
        Rec.SetCurrentKey("Sequence");
        Rec.SetAscending("Sequence", false);
        Rec.FindSet();

        // [THEN] First record has the HIGHEST Sequence (15), not the lowest
        Assert.AreEqual(15, Rec."Sequence", 'Descending: first should be Sequence=15');
        Rec.Next();
        Assert.AreEqual(10, Rec."Sequence", 'Descending: second should be Sequence=10');
        Rec.Next();
        Assert.AreEqual(5, Rec."Sequence", 'Descending: third should be Sequence=5');
    end;

    local procedure InsertKeyProbe(Code: Code[20]; Name: Text[100]; Sequence: Integer)
    var
        Rec: Record "Key Probe";
    begin
        Rec.Init();
        Rec."Code" := Code;
        Rec."Name" := Name;
        Rec."Sequence" := Sequence;
        Rec.Insert();
    end;
}
