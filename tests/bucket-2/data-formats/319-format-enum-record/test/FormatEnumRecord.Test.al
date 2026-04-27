codeunit 1316002 "FER Format Enum Record Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure EnumField_FormatsWithOwnCaption()
    var
        Rec: Record "FER Log";
        StoredCaption: Text;
        LiteralCaption: Text;
    begin
        // RED: Format(Rec.EnumField) must use the field's own enum type,
        // not another enum that happens to share the same ordinal value.
        Rec.Init();
        Rec.PK := 1;
        Rec.ActionType := "FER My Action"::SendOrder;
        Rec.Insert(false);
        Rec.Get(1);

        StoredCaption := Format(Rec.ActionType);
        LiteralCaption := Format("FER My Action"::SendOrder);

        // Both must return 'SendOrder' — NOT 'Success' (from "FER My Status" ordinal 1)
        Assert.AreEqual(LiteralCaption, StoredCaption,
            'Format(Rec.ActionType) must return the same caption as Format("FER My Action"::SendOrder)');
        Assert.AreEqual('SendOrder', StoredCaption,
            'Format(Rec.ActionType) must return SendOrder, not a caption from another enum');
    end;

    [Test]
    procedure EnumField_ZeroOrdinal_FormatsWithOwnCaption()
    var
        Rec: Record "FER Log";
        StoredCaption: Text;
    begin
        // Ordinal 0 edge case: both enums have value(0;" "), caption ' '.
        // The stored field must still format correctly.
        Rec.Init();
        Rec.PK := 2;
        Rec.ActionType := "FER My Action"::" ";
        Rec.Insert(false);
        Rec.Get(2);

        StoredCaption := Format(Rec.ActionType);
        Assert.AreEqual(' ', StoredCaption,
            'Format(Rec.ActionType) for ordinal 0 must return the space caption');
    end;

    [Test]
    procedure EnumField_LiteralAndFieldAgreement_BothEnums()
    var
        LogRec: Record "FER Log";
        ActionFmt: Text;
        StatusFmt: Text;
    begin
        // Verify that having two enums with the same ordinals in scope does
        // not corrupt each other's Format output.
        LogRec.Init();
        LogRec.PK := 3;
        LogRec.ActionType := "FER My Action"::SendOrder;
        LogRec.Insert(false);
        LogRec.Get(3);

        ActionFmt := Format(LogRec.ActionType);           // must be 'SendOrder'
        StatusFmt := Format("FER My Status"::Success);    // must be 'Success'

        Assert.AreEqual('SendOrder', ActionFmt,
            'Format of "FER My Action" field must be SendOrder');
        Assert.AreEqual('Success', StatusFmt,
            'Format of "FER My Status" literal must be Success');
        Assert.AreNotEqual(ActionFmt, StatusFmt,
            'Two different enums at same ordinal must format differently');
    end;
}
