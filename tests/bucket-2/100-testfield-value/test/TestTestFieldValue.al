codeunit 63001 "Test TestField Value"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // TestField(Field) — non-empty/non-zero check
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_Text_EmptyThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with empty Name field
        Rec.Init();
        Rec."No." := 'A';
        // Name is empty (default)

        // [WHEN/THEN] TestField(Name) throws because Name is empty
        asserterror Rec.TestField(Name);
        Assert.ExpectedTestFieldError(Rec.FieldCaption(Name), '');
    end;

    [Test]
    procedure TestField_Text_NonEmptyPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with non-empty Name field
        Rec.Init();
        Rec."No." := 'B';
        Rec.Name := 'Widget';

        // [WHEN/THEN] TestField(Name) succeeds — name has a value
        Rec.TestField(Name);
    end;

    [Test]
    procedure TestField_Integer_ZeroThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with zero Qty
        Rec.Init();
        Rec."No." := 'C';
        Rec.Qty := 0;

        // [WHEN/THEN] TestField(Qty) throws because Qty is zero
        asserterror Rec.TestField(Qty);
        Assert.ExpectedTestFieldError(Rec.FieldCaption(Qty), '');
    end;

    [Test]
    procedure TestField_Integer_NonZeroPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with non-zero Qty
        Rec.Init();
        Rec."No." := 'D';
        Rec.Qty := 5;

        // [WHEN/THEN] TestField(Qty) succeeds
        Rec.TestField(Qty);
    end;

    [Test]
    procedure TestField_Decimal_ZeroThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with zero Price
        Rec.Init();
        Rec."No." := 'E';
        Rec.Price := 0;

        // [WHEN/THEN] TestField(Price) throws because Price is zero
        asserterror Rec.TestField(Price);
        Assert.ExpectedTestFieldError(Rec.FieldCaption(Price), '');
    end;

    [Test]
    procedure TestField_Decimal_NonZeroPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with non-zero Price
        Rec.Init();
        Rec."No." := 'F';
        Rec.Price := 9.99;

        // [WHEN/THEN] TestField(Price) succeeds
        Rec.TestField(Price);
    end;

    // -----------------------------------------------------------------------
    // TestField(Field, ExpectedValue) — value equality check
    // -----------------------------------------------------------------------

    [Test]
    procedure TestField_TextValue_MatchPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with Name = 'Widget'
        Rec.Init();
        Rec."No." := 'G';
        Rec.Name := 'Widget';

        // [WHEN/THEN] TestField(Name, 'Widget') passes
        Rec.TestField(Name, 'Widget');
    end;

    [Test]
    procedure TestField_TextValue_MismatchThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with Name = 'Widget'
        Rec.Init();
        Rec."No." := 'H';
        Rec.Name := 'Widget';

        // [WHEN/THEN] TestField(Name, 'Gadget') throws — value does not match
        asserterror Rec.TestField(Name, 'Gadget');
        Assert.ExpectedTestFieldError(Rec.FieldCaption(Name), 'Gadget');
    end;

    [Test]
    procedure TestField_IntegerValue_MatchPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with Qty = 10
        Rec.Init();
        Rec."No." := 'I';
        Rec.Qty := 10;

        // [WHEN/THEN] TestField(Qty, 10) passes
        Rec.TestField(Qty, 10);
    end;

    [Test]
    procedure TestField_IntegerValue_MismatchThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with Qty = 10
        Rec.Init();
        Rec."No." := 'J';
        Rec.Qty := 10;

        // [WHEN/THEN] TestField(Qty, 5) throws — value does not match
        asserterror Rec.TestField(Qty, 5);
        Assert.ExpectedTestFieldError(Rec.FieldCaption(Qty), '5');
    end;

    [Test]
    procedure TestField_DecimalValue_MatchPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with Price = 9.99
        Rec.Init();
        Rec."No." := 'K';
        Rec.Price := 9.99;

        // [WHEN/THEN] TestField(Price, 9.99) passes
        Rec.TestField(Price, 9.99);
    end;

    [Test]
    procedure TestField_DecimalValue_MismatchThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with Price = 9.99
        Rec.Init();
        Rec."No." := 'L';
        Rec.Price := 9.99;

        // [WHEN/THEN] TestField(Price, 1.00) throws — value does not match
        asserterror Rec.TestField(Price, 1.00);
        Assert.ExpectedTestFieldError(Rec.FieldCaption(Price), '1');
    end;

    [Test]
    procedure TestField_CodeValue_MatchPasses()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with No. = 'M'
        Rec.Init();
        Rec."No." := 'M';

        // [WHEN/THEN] TestField(No., 'M') passes
        Rec.TestField("No.", 'M');
    end;

    [Test]
    procedure TestField_CodeValue_MismatchThrows()
    var
        Rec: Record "TFV Item";
    begin
        // [GIVEN] Record with No. = 'M'
        Rec.Init();
        Rec."No." := 'M';

        // [WHEN/THEN] TestField(No., 'X') throws — value does not match
        asserterror Rec.TestField("No.", 'X');
        Assert.ExpectedTestFieldError(Rec.FieldCaption("No."), 'X');
    end;
}
