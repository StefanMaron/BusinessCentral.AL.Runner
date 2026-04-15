codeunit 54200 "Test TransferFields"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TransferFieldsCopiesMatchingFieldsByNumber()
    var
        Src: Record "TF Src";
        Tgt: Record "TF Tgt";
    begin
        // Positive: fields with identical numbers are copied from source to target.
        Src.Init();
        Src."Entry No." := 7;
        Src.Name := 'Alice';
        Src.Amount := 123.45;
        Src.Active := true;
        Src.Insert(true);

        Tgt.Init();
        Tgt.TransferFields(Src);

        Assert.AreEqual('Alice', Tgt.Name, 'Name (field 2) should be copied');
        Assert.AreEqual(123.45, Tgt.Amount, 'Amount (field 3) should be copied');
    end;

    [Test]
    procedure TransferFieldsDefaultCopiesPrimaryKey()
    var
        Src: Record "TF Src";
        Tgt: Record "TF Tgt";
    begin
        // Positive: default overload (InitializeCommon omitted) copies the PK too.
        Src.Init();
        Src."Entry No." := 42;
        Src.Name := 'PkTest';
        Src.Insert(true);

        Tgt.Init();
        Tgt."Entry No." := 99;
        Tgt.TransferFields(Src);

        Assert.AreEqual(42, Tgt."Entry No.",
            'PK (field 1) should be overwritten when default InitializeCommon is used');
    end;

    [Test]
    procedure TransferFieldsWithFalsePreservesPrimaryKey()
    var
        Src: Record "TF Src";
        Tgt: Record "TF Tgt";
    begin
        // Negative: InitializeCommon=false must NOT overwrite the target's PK.
        Src.Init();
        Src."Entry No." := 11;
        Src.Name := 'KeepPk';
        Src.Amount := 50;
        Src.Insert(true);

        Tgt.Init();
        Tgt."Entry No." := 888;
        Tgt.TransferFields(Src, false);

        Assert.AreEqual(888, Tgt."Entry No.",
            'PK must be preserved when InitializeCommon=false');
        Assert.AreEqual('KeepPk', Tgt.Name,
            'Non-PK matching fields should still transfer');
        Assert.AreEqual(50, Tgt.Amount,
            'Non-PK matching fields should still transfer');
    end;

    [Test]
    procedure TransferFieldsLeavesTargetOnlyFieldUntouched()
    var
        Src: Record "TF Src";
        Tgt: Record "TF Tgt";
    begin
        // Negative: a field that only exists on the target (Extra, field 5)
        // must retain its pre-transfer value since the source has no field 5.
        Src.Init();
        Src."Entry No." := 3;
        Src.Name := 'Bob';
        Src.Amount := 20;
        Src.Insert(true);

        Tgt.Init();
        Tgt."Entry No." := 3;
        Tgt.Extra := 'PreservedValue';
        Tgt.TransferFields(Src);

        Assert.AreEqual('PreservedValue', Tgt.Extra,
            'Target-only field (no counterpart on source) must be left untouched');
        Assert.AreEqual('Bob', Tgt.Name,
            'Matching field should still transfer');
    end;
}
