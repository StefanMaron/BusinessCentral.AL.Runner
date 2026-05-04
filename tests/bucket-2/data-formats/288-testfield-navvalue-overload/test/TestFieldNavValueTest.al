/// Tests for Record.TestField(Field, Value) where Value is stored in a typed
/// variable (Integer, Decimal, Text, Boolean) — the transpiler emits a NavValue
/// subtype (NavInteger, NavDecimal, etc.) which previously caused CS0121 ambiguous
/// overload errors on ALTestFieldSafe. Regression test for issue #1018.
codeunit 161001 "TFNav Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Integer field — positive case: value matches, no error
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Integer_MatchingValue_NoError()
    var
        Rec: Record "TFNav Record";
        Expected: Integer;
    begin
        // [GIVEN] A record with Qty = 42
        Rec.Init();
        Rec.Id := 1;
        Rec.Qty := 42;
        Rec.Insert(false);

        // [WHEN] TestField is called with the same integer value in a typed variable
        // The transpiler emits ALTestFieldSafe(fieldNo, NavType.Integer, navIntegerVar)
        // which triggered CS0121 ambiguity between NavValue and int overloads.
        Expected := 42;
        Rec.TestField(Qty, Expected);

        // [THEN] No error is raised — value matches
        Assert.IsTrue(true, 'TestField must not raise an error when Integer field matches the expected value');
    end;

    // ------------------------------------------------------------------
    // Integer field — negative case: value mismatch, error expected
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Integer_MismatchValue_RaisesError()
    var
        Rec: Record "TFNav Record";
        Expected: Integer;
    begin
        // [GIVEN] A record with Qty = 99
        Rec.Init();
        Rec.Id := 2;
        Rec.Qty := 99;
        Rec.Insert(false);

        // [WHEN] TestField is called with a different integer value in a typed variable
        Expected := 7;

        // [THEN] An error is raised for the value mismatch
        asserterror Rec.TestField(Qty, Expected);
        Assert.ExpectedError('must');
    end;

    // ------------------------------------------------------------------
    // Text field — positive case: value matches, no error
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Text_MatchingValue_NoError()
    var
        Rec: Record "TFNav Record";
        Expected: Text[100];
    begin
        // [GIVEN] A record with Name = 'Acme'
        Rec.Init();
        Rec.Id := 3;
        Rec.Name := 'Acme';
        Rec.Insert(false);

        // [WHEN] TestField is called with the same text value in a typed variable
        Expected := 'Acme';
        Rec.TestField(Name, Expected);

        // [THEN] No error is raised
        Assert.IsTrue(true, 'TestField must not raise an error when Text field matches the expected value');
    end;

    // ------------------------------------------------------------------
    // Text field — negative case: value mismatch, error expected
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Text_MismatchValue_RaisesError()
    var
        Rec: Record "TFNav Record";
        Expected: Text[100];
    begin
        // [GIVEN] A record with Name = 'Acme'
        Rec.Init();
        Rec.Id := 4;
        Rec.Name := 'Acme';
        Rec.Insert(false);

        // [WHEN] TestField is called with a different text value in a typed variable
        Expected := 'Beta';

        // [THEN] An error is raised
        asserterror Rec.TestField(Name, Expected);
        Assert.ExpectedError('must');
    end;

    // ------------------------------------------------------------------
    // Decimal field — positive case: value matches, no error
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Decimal_MatchingValue_NoError()
    var
        Rec: Record "TFNav Record";
        Expected: Decimal;
    begin
        // [GIVEN] A record with Amt = 3.14
        Rec.Init();
        Rec.Id := 5;
        Rec.Amt := 3.14;
        Rec.Insert(false);

        // [WHEN] TestField is called with the same decimal value in a typed variable
        Expected := 3.14;
        Rec.TestField(Amt, Expected);

        // [THEN] No error is raised
        Assert.IsTrue(true, 'TestField must not raise an error when Decimal field matches the expected value');
    end;

    // ------------------------------------------------------------------
    // Decimal field — negative case: value mismatch, error expected
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Decimal_MismatchValue_RaisesError()
    var
        Rec: Record "TFNav Record";
        Expected: Decimal;
    begin
        // [GIVEN] A record with Amt = 3.14
        Rec.Init();
        Rec.Id := 6;
        Rec.Amt := 3.14;
        Rec.Insert(false);

        // [WHEN] TestField is called with a different decimal value in a typed variable
        Expected := 9.99;

        // [THEN] An error is raised
        asserterror Rec.TestField(Amt, Expected);
        Assert.ExpectedError('must');
    end;

    // ------------------------------------------------------------------
    // Boolean field — positive case: value matches, no error
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Boolean_MatchingValue_NoError()
    var
        Rec: Record "TFNav Record";
        Expected: Boolean;
    begin
        // [GIVEN] A record with Flag = true
        Rec.Init();
        Rec.Id := 7;
        Rec.Flag := true;
        Rec.Insert(false);

        // [WHEN] TestField is called with the same boolean value in a typed variable
        Expected := true;
        Rec.TestField(Flag, Expected);

        // [THEN] No error is raised
        Assert.IsTrue(true, 'TestField must not raise an error when Boolean field matches the expected value');
    end;

    // ------------------------------------------------------------------
    // Boolean field — negative case: value mismatch, error expected
    // ------------------------------------------------------------------

    [Test]
    procedure TestField_Boolean_MismatchValue_RaisesError()
    var
        Rec: Record "TFNav Record";
        Expected: Boolean;
    begin
        // [GIVEN] A record with Flag = true
        Rec.Init();
        Rec.Id := 8;
        Rec.Flag := true;
        Rec.Insert(false);

        // [WHEN] TestField is called with false (mismatch) in a typed variable
        Expected := false;

        // [THEN] An error is raised
        asserterror Rec.TestField(Flag, Expected);
        Assert.ExpectedError('must');
    end;
}
