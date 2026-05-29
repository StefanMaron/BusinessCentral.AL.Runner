codeunit 61001 "Microsoft Dependency Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "MD Assert";
        LibraryNoSeries: Codeunit "Library - No. Series";

    [Test]
    procedure BaseAppTable_PaymentMethod_CanInsertAndRead()
    var
        PaymentMethod: Record "Payment Method";
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'ALR-PM';
        PaymentMethod.Description := 'AL Runner dependency metadata regression';
        PaymentMethod.Insert(true);

        Clear(PaymentMethod);
        Assert.IsTrue(PaymentMethod.Get('ALR-PM'), 'Base Application table 289 must be runtime-loadable.');
        Assert.IsTrue(PaymentMethod.Description = 'AL Runner dependency metadata regression',
            'Inserted Base Application table data must round-trip.');
    end;

    [Test]
    procedure BaseAppTable_NoSeriesLine_CanInsert()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
    begin
        NoSeries.Init();
        NoSeries.Code := 'ALRUNNER';
        NoSeries.Insert(true);

        NoSeriesLine.Init();
        NoSeriesLine."Series Code" := NoSeries.Code;
        NoSeriesLine."Line No." := 10000;
        NoSeriesLine."Starting No." := 'A0001';
        NoSeriesLine."Ending No." := 'A9999';
        NoSeriesLine."Increment-by No." := 1;
        NoSeriesLine.Insert(true);

        Clear(NoSeriesLine);
        Assert.IsTrue(NoSeriesLine.Get('ALRUNNER', 10000), 'No. Series Line must be runtime-loadable.');
    end;

    [Test]
    procedure BaseAppTable_RecordRefFilteredIsEmpty_SeesRange()
    var
        PaymentMethod: Record "Payment Method";
        RecRef: RecordRef;
        FieldRef: FieldRef;
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'ALR-EMPTY1';
        PaymentMethod.Description := 'AL Runner dependency metadata regression';
        PaymentMethod.Insert(true);

        RecRef.Open(Database::"Payment Method");
        FieldRef := RecRef.Field(PaymentMethod.FieldNo(Code));
        FieldRef.SetRange('NOEXIST');

        Assert.IsTrue(RecRef.IsEmpty(), 'RecordRef.IsEmpty must respect FieldRef.SetRange on dependency tables.');
    end;

    [Test]
    procedure BaseAppTable_RecordRefFilteredFindFirst_SeesRange()
    var
        PaymentMethod: Record "Payment Method";
        RecRef: RecordRef;
        FieldRef: FieldRef;
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'ALR-EMPTY2';
        PaymentMethod.Description := 'AL Runner dependency metadata regression';
        PaymentMethod.Insert(true);

        RecRef.Open(Database::"Payment Method");
        FieldRef := RecRef.Field(PaymentMethod.FieldNo(Code));
        FieldRef.SetRange('NOEXIST');

        Assert.IsTrue(not RecRef.FindFirst(), 'RecordRef.FindFirst must respect FieldRef.SetRange on dependency tables.');
    end;

    [Test]
    procedure BaseAppCodeunit_NoSeries_GetNextNo_Completes()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
        NoSeriesCodeunit: Codeunit "No. Series";
        NextNo: Code[20];
    begin
        NoSeries.Code := 'ALR-GUID';
        NoSeries."Default Nos." := true;
        NoSeries.Insert();

        NoSeriesLine."Series Code" := NoSeries.Code;
        NoSeriesLine."Line No." := 10000;
        NoSeriesLine."Starting No." := 'ALG0000001';
        NoSeriesLine."Ending No." := 'ALG9999999';
        NoSeriesLine."Increment-by No." := 1;
        NoSeriesLine.Insert(true);

        NextNo := NoSeriesCodeunit.GetNextNo(NoSeries.Code);

        Assert.IsTrue(NextNo <> '', 'No. Series codeunit should return a number.');
    end;

    [Test]
    procedure BaseAppCodeunit_LibraryNoSeries_CreateNoSeriesLine_Completes()
    var
        NoSeries: Record "No. Series";
        NoSeriesLine: Record "No. Series Line";
    begin
        NoSeries.Code := 'ALRLIB';
        NoSeries.Insert();

        LibraryNoSeries.CreateNoSeriesLine('ALRLIB', 1, 'ALL0000001', 'ALL9999999');

        Assert.IsTrue(NoSeriesLine.Get('ALRLIB', 10000), 'Library - No. Series should create a No. Series Line.');
    end;

}
