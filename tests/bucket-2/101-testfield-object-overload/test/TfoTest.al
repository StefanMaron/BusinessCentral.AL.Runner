/// Tests for TestField on Integer fields inside table procedures (ALTestFieldNavValueSafe object overload).
/// Reproduces CS1503 'object' → 'NavValue' from issue #1324.
codeunit 307101 "TFO Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Integer field — no-value form (Rec.TestField("Table No."))
    // -----------------------------------------------------------------------

    [Test]
    procedure TableNo_NonZero_TestFieldPasses()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Table No. = 42
        Tbl.Init();
        Tbl."No." := 'A';
        Tbl."Table No." := 42;
        Tbl.Insert();

        // [WHEN/THEN] TestField("Table No.") inside table procedure must not throw
        Tbl.ValidateTableNo();
    end;

    [Test]
    procedure TableNo_Zero_TestFieldThrows()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Table No. = 0 (default)
        Tbl.Init();
        Tbl."No." := 'B';
        Tbl."Table No." := 0;
        Tbl.Insert();

        // [WHEN/THEN] TestField("Table No.") inside table procedure must throw (zero is empty)
        asserterror Tbl.ValidateTableNo();
        Assert.ExpectedTestFieldError(Tbl.FieldCaption("Table No."), '');
    end;

    // -----------------------------------------------------------------------
    // Integer field — value form (Rec.TestField("Table No.", ExpectedNo))
    // -----------------------------------------------------------------------

    [Test]
    procedure TableNo_MatchingValue_TestFieldPasses()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Table No. = 99
        Tbl.Init();
        Tbl."No." := 'C';
        Tbl."Table No." := 99;
        Tbl.Insert();

        // [WHEN/THEN] ValidateTableNoEquals(99) must not throw
        Tbl.ValidateTableNoEquals(99);
    end;

    [Test]
    procedure TableNo_MismatchValue_TestFieldThrows()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Table No. = 99
        Tbl.Init();
        Tbl."No." := 'D';
        Tbl."Table No." := 99;
        Tbl.Insert();

        // [WHEN/THEN] ValidateTableNoEquals(1) must throw — value does not match
        asserterror Tbl.ValidateTableNoEquals(1);
        Assert.ExpectedTestFieldError(Tbl.FieldCaption("Table No."), '1');
    end;

    // -----------------------------------------------------------------------
    // Text field — value form (Rec.TestField("Name", ExpectedName)) inside table
    // Exercises ALTestFieldNavValueSafe(object) for Text type.
    // -----------------------------------------------------------------------

    [Test]
    procedure Name_MatchingValue_TestFieldPasses()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Name = 'Widget'
        Tbl.Init();
        Tbl."No." := 'E';
        Tbl."Name" := 'Widget';
        Tbl.Insert();

        // [WHEN/THEN] ValidateNameEquals('Widget') must not throw
        Tbl.ValidateNameEquals('Widget');
    end;

    [Test]
    procedure Name_MismatchValue_TestFieldThrows()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Name = 'Widget'
        Tbl.Init();
        Tbl."No." := 'F';
        Tbl."Name" := 'Widget';
        Tbl.Insert();

        // [WHEN/THEN] ValidateNameEquals('Gadget') must throw — value does not match
        asserterror Tbl.ValidateNameEquals('Gadget');
        Assert.ExpectedTestFieldError(Tbl.FieldCaption("Name"), 'Gadget');
    end;

    // -----------------------------------------------------------------------
    // Text field — no-value form (Rec.TestField("Name")) inside table
    // -----------------------------------------------------------------------

    [Test]
    procedure Name_NonEmpty_TestFieldPasses()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Name = 'Test'
        Tbl.Init();
        Tbl."No." := 'G';
        Tbl."Name" := 'Test';
        Tbl.Insert();

        // [WHEN/THEN] ValidateName() must not throw
        Tbl.ValidateName();
    end;

    [Test]
    procedure Name_Empty_TestFieldThrows()
    var
        Tbl: Record "TFO Table";
    begin
        // [GIVEN] Record with Name = '' (empty)
        Tbl.Init();
        Tbl."No." := 'H';
        // Name is intentionally empty
        Tbl.Insert();

        // [WHEN/THEN] ValidateName() must throw — empty text is considered empty
        asserterror Tbl.ValidateName();
        Assert.ExpectedTestFieldError(Tbl.FieldCaption("Name"), '');
    end;
}
