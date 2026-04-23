codeunit 295002 "Auto Increment Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    [Test]
    procedure InsertWithZero_AutoIncrements()
    var
        Entry: Record "Auto Inc Entry";
    begin
        // [GIVEN] A clean table with an AutoIncrement field
        Entry.DeleteAll();
        // [WHEN] Insert with EntryNo = 0
        Entry.Init();
        Entry."Entry No." := 0;
        Entry.Description := 'First';
        Entry.Insert();
        // [THEN] EntryNo is auto-assigned to 1
        Assert.AreEqual(1, Entry."Entry No.", 'First insert should auto-increment to 1');
    end;

    [Test]
    procedure InsertMultiple_AutoIncrements_Sequentially()
    var
        Entry: Record "Auto Inc Entry";
    begin
        // [GIVEN] A clean table with AutoIncrement
        Entry.DeleteAll();
        // [WHEN] Insert three records with EntryNo = 0
        Entry.Init();
        Entry."Entry No." := 0;
        Entry.Description := 'First';
        Entry.Insert();
        Assert.AreEqual(1, Entry."Entry No.", 'First should be 1');

        Entry.Init();
        Entry."Entry No." := 0;
        Entry.Description := 'Second';
        Entry.Insert();
        Assert.AreEqual(2, Entry."Entry No.", 'Second should be 2');

        Entry.Init();
        Entry."Entry No." := 0;
        Entry.Description := 'Third';
        Entry.Insert();
        Assert.AreEqual(3, Entry."Entry No.", 'Third should be 3');
    end;

    [Test]
    procedure InsertWithExplicitValue_DoesNotAutoIncrement()
    var
        Entry: Record "Auto Inc Entry";
    begin
        // [GIVEN] A clean table with AutoIncrement
        Entry.DeleteAll();
        // [WHEN] Insert with an explicit non-zero EntryNo
        Entry.Init();
        Entry."Entry No." := 42;
        Entry.Description := 'Explicit';
        Entry.Insert();
        // [THEN] EntryNo stays at the explicit value
        Assert.AreEqual(42, Entry."Entry No.", 'Explicit value should be preserved');
    end;

    [Test]
    procedure InsertAfterExplicit_ContinuesFromMax()
    var
        Entry: Record "Auto Inc Entry";
    begin
        // [GIVEN] A clean table with explicit EntryNo = 10
        Entry.DeleteAll();
        Entry.Init();
        Entry."Entry No." := 10;
        Entry.Description := 'Explicit';
        Entry.Insert();

        // [WHEN] Insert another with EntryNo = 0
        Entry.Init();
        Entry."Entry No." := 0;
        Entry.Description := 'Auto';
        Entry.Insert();
        // [THEN] EntryNo is max(existing) + 1 = 11
        Assert.AreEqual(11, Entry."Entry No.", 'Should continue from max existing value');
    end;

    [Test]
    procedure InsertWithZero_NoDuplicate()
    var
        Entry: Record "Auto Inc Entry";
        Entry2: Record "Auto Inc Entry";
    begin
        // [GIVEN] A clean table
        Entry.DeleteAll();
        // [WHEN] Two records inserted with EntryNo = 0
        Entry.Init();
        Entry."Entry No." := 0;
        Entry.Description := 'A';
        Entry.Insert();

        Entry2.Init();
        Entry2."Entry No." := 0;
        Entry2.Description := 'B';
        Entry2.Insert();

        // [THEN] They got different EntryNos (no duplicate key error)
        Assert.AreNotEqual(Entry."Entry No.", Entry2."Entry No.",
            'Auto-incremented records must have different primary keys');
    end;
}
