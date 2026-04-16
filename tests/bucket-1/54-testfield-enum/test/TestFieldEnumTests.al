codeunit 56101 "TestField Enum Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure MakeOrder(No: Code[20]; Status: Enum "TFE Status")
    var
        Ord: Record "TFE Order";
    begin
        Ord.DeleteAll();
        Ord.Init();
        Ord."No." := No;
        Ord.Status := Status;
        Ord.Insert();
    end;

    // -----------------------------------------------------------------------
    // TestField(Field, EnumValue) — positive: matching value passes
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_EnumMatchingValue_NoError()
    var
        Ord: Record "TFE Order";
    begin
        // [GIVEN] An order with Status = Active
        MakeOrder('ORD-1', "TFE Status"::Active);
        Ord.Get('ORD-1');

        // [WHEN/THEN] TestField with exact matching enum value — no error raised
        Ord.TestField(Status, "TFE Status"::Active);
    end;

    [Test]
    procedure TestField_EnumMatchingClosedValue_NoError()
    var
        Ord: Record "TFE Order";
    begin
        // [GIVEN] An order with Status = Closed
        MakeOrder('ORD-2', "TFE Status"::Closed);
        Ord.Get('ORD-2');

        // [WHEN/THEN] TestField with matching Closed enum value — no error
        Ord.TestField(Status, "TFE Status"::Closed);
    end;

    // -----------------------------------------------------------------------
    // TestField(Field, EnumValue) — negative: wrong value errors
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_EnumWrongValue_Errors()
    var
        Ord: Record "TFE Order";
    begin
        // [GIVEN] An order with Status = Active
        MakeOrder('ORD-3', "TFE Status"::Active);
        Ord.Get('ORD-3');

        // [WHEN] TestField expects Closed but Status is Active
        asserterror Ord.TestField(Status, "TFE Status"::Closed);

        // [THEN] Error is raised (TestField failure message)
        Assert.ExpectedError('TestField');
    end;

    [Test]
    procedure TestField_EnumDefaultExpectedButNonZero_Errors()
    var
        Ord: Record "TFE Order";
    begin
        // [GIVEN] An order with Status = Active (ordinal 1)
        MakeOrder('ORD-4', "TFE Status"::Active);
        Ord.Get('ORD-4');

        // [WHEN] TestField expects Draft (ordinal 0) but field is Active
        asserterror Ord.TestField(Status, "TFE Status"::Draft);

        // [THEN] Error is raised
        Assert.ExpectedError('TestField');
    end;

    // -----------------------------------------------------------------------
    // TestField(Field) without expected value — non-zero check
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_EnumNonDefault_NoError()
    var
        Ord: Record "TFE Order";
    begin
        // [GIVEN] An order with Status = Active (non-zero)
        MakeOrder('ORD-5', "TFE Status"::Active);
        Ord.Get('ORD-5');

        // [WHEN/THEN] TestField(Status) passes since value is non-default
        Ord.TestField(Status);
    end;

    [Test]
    procedure TestField_EnumAtDefault_Errors()
    var
        Ord: Record "TFE Order";
    begin
        // [GIVEN] An order with Status = Draft (ordinal 0 — default)
        MakeOrder('ORD-6', "TFE Status"::Draft);
        Ord.Get('ORD-6');

        // [WHEN] TestField(Status) — must have a non-default value
        asserterror Ord.TestField(Status);

        // [THEN] Error is raised since Status is at default value
        Assert.ExpectedError('TestField');
    end;
}
