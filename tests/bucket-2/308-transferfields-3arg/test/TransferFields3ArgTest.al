codeunit 308301 "TransferFields 3-Arg Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // 3-arg TransferFields — positive: copies fields without validation
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFields3ArgCopiesFieldsWithValidateFalse()
    var
        Src: Record "TF3Arg Record";
        Tgt: Record "TF3Arg Record";
    begin
        // [GIVEN] A source record with Amount = 42
        Src.Init();
        Src."Entry No." := 1;
        Src."Name" := 'Alice';
        Src."Amount" := 42;

        Tgt.Init();
        Tgt."Entry No." := 99;

        // [WHEN] TransferFields is called with validateFields = false (3-arg form)
        Tgt.TransferFields(Src, true, false);

        // [THEN] Fields are copied; PK was transferred (initPrimaryKey=true)
        Assert.AreEqual(1, Tgt."Entry No.", 'PK should be copied when initPrimaryKey=true');
        Assert.AreEqual('Alice', Tgt."Name", 'Name should be transferred');
        Assert.AreEqual(42, Tgt."Amount", 'Amount should be transferred');
    end;

    // -----------------------------------------------------------------------
    // 3-arg TransferFields — positive: PK preserved when initPrimaryKey=false
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFields3ArgPreservesPKWhenInitPKFalse()
    var
        Src: Record "TF3Arg Record";
        Tgt: Record "TF3Arg Record";
    begin
        // [GIVEN] Source has Entry No. = 5, Target has Entry No. = 99
        Src.Init();
        Src."Entry No." := 5;
        Src."Name" := 'Bob';
        Src."Amount" := 100;

        Tgt.Init();
        Tgt."Entry No." := 99;

        // [WHEN] TransferFields with initPrimaryKey=false, validateFields=false (3-arg)
        Tgt.TransferFields(Src, false, false);

        // [THEN] PK should NOT be overwritten
        Assert.AreEqual(99, Tgt."Entry No.", 'PK should be preserved when initPrimaryKey=false');
        Assert.AreEqual('Bob', Tgt."Name", 'Name should be transferred');
        Assert.AreEqual(100, Tgt."Amount", 'Amount should be transferred');
    end;

    // -----------------------------------------------------------------------
    // 3-arg TransferFields — negative: validateFields=true triggers validation
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFields3ArgValidatesFieldsWhenValidateTrue()
    var
        Src: Record "TF3Arg Record";
        Tgt: Record "TF3Arg Record";
    begin
        // [GIVEN] Source record with Amount = -10 (invalid per OnValidate)
        Src.Init();
        Src."Entry No." := 10;
        Src."Name" := 'Charlie';
        Src."Amount" := -10;

        Tgt.Init();
        Tgt."Entry No." := 20;

        // [WHEN] TransferFields is called with validateFields = true
        // [THEN] Validation error should fire because Amount < 0
        asserterror Tgt.TransferFields(Src, true, true);
        Assert.ExpectedError('Amount must not be negative');
    end;
}
