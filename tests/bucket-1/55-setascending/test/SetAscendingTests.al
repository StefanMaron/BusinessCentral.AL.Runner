codeunit 56201 "SetAscending Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure Seed()
    var
        Item: Record "SA Item";
    begin
        Item.DeleteAll();
        // Inserted out-of-order to prove sorting is by key, not insertion order
        // Code: C/B/A, Name: Charlie/Bravo/Alpha, Priority: 2/1/3
        Item.Init(); Item.Code := 'C'; Item.Name := 'Charlie'; Item.Priority := 2; Item.Insert();
        Item.Init(); Item.Code := 'B'; Item.Name := 'Bravo';   Item.Priority := 1; Item.Insert();
        Item.Init(); Item.Code := 'A'; Item.Name := 'Alpha';   Item.Priority := 3; Item.Insert();
    end;

    // -----------------------------------------------------------------------
    // Default ascending (no SetAscending call)
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAscending_DefaultPKAscending()
    var
        Item: Record "SA Item";
        Codes: Text;
    begin
        // [GIVEN] Three records inserted in non-PK order
        Seed();

        // [WHEN] FindSet with no SetAscending — default is ascending
        Item.FindSet();
        repeat
            Codes += Item.Code;
        until Item.Next() = 0;

        // [THEN] Codes come out A B C (PK ascending)
        Assert.AreEqual('ABC', Codes, 'Default sort must be PK ascending');
    end;

    // -----------------------------------------------------------------------
    // SetAscending(field, false) — descending order
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAscending_NameDescending_IteratesZtoA()
    var
        Item: Record "SA Item";
        Names: Text;
    begin
        // [GIVEN] Three records with distinct Names
        Seed();

        // [WHEN] SetCurrentKey(Name), SetAscending(Name, false), FindSet
        Item.SetCurrentKey(Name);
        Item.SetAscending(Name, false);
        Item.FindSet();
        repeat
            Names += Item.Name + '|';
        until Item.Next() = 0;

        // [THEN] Names come out Charlie|Bravo|Alpha| (Name descending)
        Assert.AreEqual('Charlie|Bravo|Alpha|', Names, 'SetAscending(Name, false) must iterate Name descending');
    end;

    [Test]
    procedure SetAscending_NameAscending_IteratesAtoZ()
    var
        Item: Record "SA Item";
        Names: Text;
    begin
        // [GIVEN] Three records
        Seed();

        // [WHEN] SetCurrentKey(Name), SetAscending(Name, true) — explicit ascending
        Item.SetCurrentKey(Name);
        Item.SetAscending(Name, true);
        Item.FindSet();
        repeat
            Names += Item.Name + '|';
        until Item.Next() = 0;

        // [THEN] Names come out Alpha|Bravo|Charlie| (Name ascending)
        Assert.AreEqual('Alpha|Bravo|Charlie|', Names, 'SetAscending(Name, true) must iterate Name ascending');
    end;

    // -----------------------------------------------------------------------
    // SetAscending affects only the specified field — composite key
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAscending_CompositeKey_PriorityAscCodeDesc()
    var
        Item: Record "SA Item";
        Items: Text;
    begin
        // [GIVEN] Four records: A(P=1), B(P=1), C(P=2), D(P=2)
        Seed();
        Item.Init(); Item.Code := 'D'; Item.Name := 'Delta'; Item.Priority := 2; Item.Insert();

        // [WHEN] SetCurrentKey(Priority, Code), Priority ascending (default), Code descending
        Item.SetCurrentKey(Priority, Code);
        Item.SetAscending(Priority, true);
        Item.SetAscending(Code, false);
        Item.FindSet();
        repeat
            Items += Item.Code;
        until Item.Next() = 0;

        // [THEN] Priority 1 first (B, A — Code desc), then Priority 2 (D, C — Code desc)
        Assert.AreEqual('BADC', Items, 'Composite key: Priority asc, Code desc must give BADC');
    end;

    // -----------------------------------------------------------------------
    // Reset clears ascending setting
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAscending_ResetClearsDirection()
    var
        Item: Record "SA Item";
        Codes: Text;
    begin
        // [GIVEN] Records, SetAscending(Code, false) set to descending
        Seed();
        Item.SetCurrentKey(Code);
        Item.SetAscending(Code, false);

        // Confirm descending first
        Item.FindSet();
        repeat
            Codes += Item.Code;
        until Item.Next() = 0;
        Assert.AreEqual('CBA', Codes, 'Before Reset: descending must give CBA');

        // [WHEN] Reset is called
        Item.Reset();

        // [THEN] Default ascending is restored after Reset
        Codes := '';
        Item.FindSet();
        repeat
            Codes += Item.Code;
        until Item.Next() = 0;
        Assert.AreEqual('ABC', Codes, 'After Reset: ascending must be restored, giving ABC');
    end;

    // -----------------------------------------------------------------------
    // FindLast respects SetAscending
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAscending_FindLast_WithDescendingKey()
    var
        Item: Record "SA Item";
    begin
        // [GIVEN] A=Alpha, B=Bravo, C=Charlie
        Seed();

        // [WHEN] SetCurrentKey(Name), SetAscending(Name, false) — Name desc
        // FindLast in Name-desc order: the "last" is the smallest name = Alpha
        Item.SetCurrentKey(Name);
        Item.SetAscending(Name, false);
        Item.FindLast();

        // [THEN] FindLast in descending-name order yields Alpha (lowest name = last position)
        Assert.AreEqual('A', Item.Code, 'FindLast with Name descending must return Alpha (Code=A)');
    end;
}
