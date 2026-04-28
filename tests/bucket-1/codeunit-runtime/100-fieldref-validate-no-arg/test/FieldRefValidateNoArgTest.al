codeunit 1320512 "FR Validate NoArg Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure FieldRef_Validate_NoArg_UsesCurrentValue()
    var
        ReproRec: Record "FR Validate NoArg";
        Rr: RecordRef;
        Fr: FieldRef;
    begin
        ReproRec.Init();
        ReproRec."No." := 1;
        ReproRec.Insert();
        Rr.GetTable(ReproRec);
        Fr := Rr.Field(2);

        Fr.Value(12.5);
        Fr.Validate();
        Rr.Modify();
        Rr.Close();

        ReproRec.Get(1);
        Assert.AreEqual(12.5, ReproRec.Amount, 'FieldRef.Validate() must keep the current value');
        Assert.IsTrue(ReproRec.Validated, 'FieldRef.Validate() must invoke OnValidate');
    end;

    [Test]
    procedure FieldRef_Validate_NoArg_InvalidValue_Errors()
    var
        ReproRec: Record "FR Validate NoArg";
        Rr: RecordRef;
        Fr: FieldRef;
    begin
        ReproRec.Init();
        ReproRec."No." := 2;
        ReproRec.Insert();
        Rr.GetTable(ReproRec);
        Fr := Rr.Field(2);

        Fr.Value(-1);
        asserterror Fr.Validate();
        Assert.ExpectedError('Amount must be non-negative');
    end;

    [Test]
    procedure FieldRef_Validate_NoArg_ChainedCall_UsesCurrentValue()
    var
        ReproRec: Record "FR Validate NoArg";
        Rr: RecordRef;
    begin
        ReproRec.Init();
        ReproRec."No." := 3;
        ReproRec.Insert();
        Rr.GetTable(ReproRec);
        GetFieldRef(Rr, 2).Value(7.5);
        GetFieldRef(Rr, 2).Validate();
        Rr.Modify();
        Rr.Close();

        ReproRec.Get(3);
        Assert.AreEqual(7.5, ReproRec.Amount, 'Chained FieldRef.Validate() must keep the current value');
        Assert.IsTrue(ReproRec.Validated, 'Chained FieldRef.Validate() must invoke OnValidate');
    end;


    local procedure GetFieldRef(var RecRef: RecordRef; FieldNo: Integer): FieldRef
    var
        Fr: FieldRef;
    begin
        Fr := RecRef.Field(FieldNo);
        exit(Fr);
    end;
}
