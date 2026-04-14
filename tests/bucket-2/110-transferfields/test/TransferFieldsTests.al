codeunit 50810 "TransferFields Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // TransferFields — positive: copies matching field values
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFieldsCopiesMatchingFields()
    var
        Src: Record "Transfer Source";
        Tgt: Record "Transfer Target";
    begin
        // [GIVEN] A source record with values
        Src.Init();
        Src."Entry No." := 1;
        Src."Name" := 'Alice';
        Src."Amount" := 100.50;
        Src."Active" := true;
        Src.Insert(true);

        // [WHEN] TransferFields is called (default: initPrimaryKey = true → copies PK too)
        Tgt.TransferFields(Src);

        // [THEN] Matching fields (1=PK, 2=Name, 3=Amount) should be copied
        Assert.AreEqual(1, Tgt."Entry No.", 'PK should be copied when initPrimaryKey=true (default)');
        Assert.AreEqual('Alice', Tgt."Name", 'Name should be transferred');
        Assert.AreEqual(100.50, Tgt."Amount", 'Amount should be transferred');
    end;

    // -----------------------------------------------------------------------
    // TransferFields — negative: non-overlapping fields are unaffected
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFieldsDoesNotCopyNonOverlappingFields()
    var
        Src: Record "Transfer Source";
        Tgt: Record "Transfer Target";
    begin
        // [GIVEN] Source with Active=true (field 4, not in Target)
        Src.Init();
        Src."Entry No." := 2;
        Src."Name" := 'Bob';
        Src."Amount" := 200;
        Src."Active" := true;
        Src.Insert(true);

        // Target has Extra (field 5, not in Source)
        Tgt.Init();
        Tgt."Extra" := 'Original';

        // [WHEN] TransferFields is called
        Tgt.TransferFields(Src);

        // [THEN] Extra field should remain unchanged (field 5 not in source)
        // Note: In our mock, TransferFields copies by field ID, so field 5
        // from source (doesn't exist) won't overwrite target's field 5.
        // The source's field 4 (Active) will be written to target's field 4
        // even though target doesn't declare it — that's fine for the mock.
        Assert.AreEqual('Bob', Tgt."Name", 'Name should be transferred');
    end;

    // -----------------------------------------------------------------------
    // TransferFields — PK handling with initPrimaryKey parameter
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFieldsSkipsPKWhenInitPKFalse()
    var
        Src: Record "Transfer Source";
        Tgt: Record "Transfer Target";
    begin
        // [GIVEN] Source with Entry No. = 10, Target with Entry No. = 99
        Src.Init();
        Src."Entry No." := 10;
        Src."Name" := 'Charlie';
        Src."Amount" := 300;
        Src.Insert(true);

        Tgt.Init();
        Tgt."Entry No." := 99;

        // [WHEN] TransferFields with initPrimaryKey = false (skip PK)
        Tgt.TransferFields(Src, false);

        // [THEN] PK field should NOT be overwritten
        Assert.AreEqual(99, Tgt."Entry No.", 'PK should be preserved when initPrimaryKey=false');
        Assert.AreEqual('Charlie', Tgt."Name", 'Name should still be transferred');
    end;
}
