codeunit 1320612 "Record Code Unwrap Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TakeCode_ReceivesRecordField()
    var
        Rec: Record "Record Code Unwrap Table";
        Helper: Codeunit "Record Code Unwrap Helper";
        Result: Code[20];
    begin
        Rec.Init();
        Rec.Code := 'BR001';
        Rec.Description := 'Branch';
        Rec.Insert(false);

        Helper.AppendSuffixFromRecord(Rec);
        Result := Rec.Code;

        Assert.AreEqual('BR001X', Result, 'Code field should be updated via var Code parameter');
    end;

    [Test]
    procedure TakeCode_VariantRecord_UsesPrimaryKey()
    var
        Rec: Record "Record Code Unwrap Table";
        Helper: Codeunit "Record Code Unwrap Helper";
        Any: Variant;
        Result: Code[20];
    begin
        Rec.Init();
        Rec.Code := 'VR001';
        Rec.Insert(false);

        Any := Rec;
        Result := Helper.TakeCode(Any);

        Assert.AreEqual('VR001', Result, 'Variant record should coerce to its Code primary key');
    end;

    [Test]
    procedure TakeCode_EmptyCodeErrors()
    var
        Rec: Record "Record Code Unwrap Table";
        Helper: Codeunit "Record Code Unwrap Helper";
    begin
        Rec.Init();

        asserterror Helper.AppendSuffixFromRecord(Rec);
        Assert.ExpectedError('Code must be provided');
    end;
}
