codeunit 56001 "FindSet Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure Seed()
    var
        Item: Record "FS Item";
    begin
        Item.DeleteAll();
        // Insert in non-alphabetical, non-priority order to prove sorting
        Item.Init(); Item."No." := 'C'; Item.Name := 'Charlie'; Item.Priority := 2; Item.Insert();
        Item.Init(); Item."No." := 'A'; Item.Name := 'Alpha';   Item.Priority := 3; Item.Insert();
        Item.Init(); Item."No." := 'B'; Item.Name := 'Bravo';   Item.Priority := 1; Item.Insert();
    end;

    // -----------------------------------------------------------------------
    // FindSet — PK order (default, no SetCurrentKey)
    // -----------------------------------------------------------------------

    [Test]
    procedure FindSet_NoPKOrder_IteratesInPKOrder()
    var
        Item: Record "FS Item";
        Codes: Text;
    begin
        // [GIVEN] Three records inserted out-of-PK order
        Seed();

        // [WHEN] FindSet with no SetCurrentKey, iterate with Next
        Item.FindSet();
        repeat
            Codes += Item."No.";
        until Item.Next() = 0;

        // [THEN] Codes come out A B C (PK ascending)
        Assert.AreEqual('ABC', Codes, 'FindSet without SetCurrentKey must iterate in PK order');
    end;

    [Test]
    procedure FindSet_WithSetCurrentKey_IteratesInAlternateKeyOrder()
    var
        Item: Record "FS Item";
        Names: Text;
    begin
        // [GIVEN] Three records: C=Charlie, A=Alpha, B=Bravo
        Seed();

        // [WHEN] SetCurrentKey to Name, then FindSet
        Item.SetCurrentKey(Name);
        Item.FindSet();
        repeat
            Names += Item.Name + '|';
        until Item.Next() = 0;

        // [THEN] Names come out Alpha|Bravo|Charlie| (Name ascending)
        Assert.AreEqual('Alpha|Bravo|Charlie|', Names, 'FindSet with SetCurrentKey(Name) must iterate in Name order');
    end;

    [Test]
    procedure FindSet_WithSetCurrentKeyPriority_IteratesInPriorityOrder()
    var
        Item: Record "FS Item";
        Codes: Text;
    begin
        // [GIVEN] A=Priority 3, B=Priority 1, C=Priority 2
        Seed();

        // [WHEN] SetCurrentKey to Priority
        Item.SetCurrentKey(Priority);
        Item.FindSet();
        repeat
            Codes += Item."No.";
        until Item.Next() = 0;

        // [THEN] B(1) C(2) A(3) — sorted by Priority ascending
        Assert.AreEqual('BCA', Codes, 'FindSet with SetCurrentKey(Priority) must iterate in Priority order');
    end;

    // -----------------------------------------------------------------------
    // FindSet — with filters
    // -----------------------------------------------------------------------

    [Test]
    procedure FindSet_WithFilterAndSetCurrentKey_RespectsFilterAndOrder()
    var
        Item: Record "FS Item";
        Codes: Text;
    begin
        // [GIVEN] Four records: A(P=3), B(P=1), C(P=2), D(P=2)
        Seed();
        Item.Init(); Item."No." := 'D'; Item.Name := 'Delta'; Item.Priority := 2; Item.Insert();

        // [WHEN] Filter to Priority >= 2, SetCurrentKey(Priority), FindSet
        Item.SetCurrentKey(Priority);
        Item.SetFilter(Priority, '>=2');
        Item.FindSet();
        repeat
            Codes += Item."No.";
        until Item.Next() = 0;

        // [THEN] C and D (P=2) then A (P=3), ordered by Priority then PK
        Assert.AreEqual('CDA', Codes, 'FindSet with filter and SetCurrentKey must respect both filter and sort order');
    end;

    [Test]
    procedure FindSet_ReturnsTrueWhenRecordsExist()
    var
        Item: Record "FS Item";
    begin
        // [GIVEN] At least one record
        Seed();

        // [WHEN/THEN] FindSet returns true
        Assert.IsTrue(Item.FindSet(), 'FindSet must return true when records exist');
    end;

    // -----------------------------------------------------------------------
    // FindSet — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure FindSet_EmptyTable_ReturnsFalse()
    var
        Item: Record "FS Item";
    begin
        // [GIVEN] Empty table
        Item.DeleteAll();

        // [WHEN/THEN] FindSet returns false
        Assert.IsFalse(Item.FindSet(), 'FindSet on empty table must return false');
    end;

    [Test]
    procedure FindSet_FilterMatchesNothing_ReturnsFalse()
    var
        Item: Record "FS Item";
    begin
        // [GIVEN] Records exist but none match Priority=999
        Seed();

        // [WHEN] SetRange to non-existent priority
        Item.SetRange(Priority, 999);

        // [THEN] FindSet returns false
        Assert.IsFalse(Item.FindSet(), 'FindSet with no matching records must return false');
    end;

    // -----------------------------------------------------------------------
    // FindLast — respects SetCurrentKey
    // -----------------------------------------------------------------------

    [Test]
    procedure FindLast_WithSetCurrentKey_ReturnsLastInAlternateKeyOrder()
    var
        Item: Record "FS Item";
    begin
        // [GIVEN] A=Alpha, B=Bravo, C=Charlie sorted by Name: Alpha < Bravo < Charlie
        Seed();

        // [WHEN] SetCurrentKey(Name), FindLast
        Item.SetCurrentKey(Name);
        Assert.IsTrue(Item.FindLast(), 'FindLast with SetCurrentKey must return true');

        // [THEN] Last by Name order is Charlie
        Assert.AreEqual('C', Item."No.", 'FindLast with SetCurrentKey(Name) must return C (Charlie = last by Name)');
    end;
}
