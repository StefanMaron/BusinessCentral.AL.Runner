codeunit 60201 "EVL Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: Evaluate parses valid text into the correct typed value.
    // ------------------------------------------------------------------

    [Test]
    procedure Evaluate_Integer_Positive()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.AreEqual(42, H.ParseInteger('42'), 'ParseInteger(''42'') must return 42');
    end;

    [Test]
    procedure Evaluate_Integer_Zero()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.AreEqual(0, H.ParseInteger('0'), 'ParseInteger(''0'') must return 0');
    end;

    [Test]
    procedure Evaluate_Integer_Negative()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.AreEqual(-7, H.ParseInteger('-7'), 'ParseInteger(''-7'') must return -7');
    end;

    [Test]
    procedure Evaluate_Integer_LargeValue()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.AreEqual(1000000, H.ParseInteger('1000000'), 'ParseInteger must handle large values');
    end;

    [Test]
    procedure Evaluate_Boolean_True()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.IsTrue(H.ParseBoolean('true'), 'ParseBoolean(''true'') must return true');
    end;

    [Test]
    procedure Evaluate_Boolean_False()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.IsFalse(H.ParseBoolean('false'), 'ParseBoolean(''false'') must return false');
    end;

    [Test]
    procedure Evaluate_Decimal_Positive()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.AreEqual(3.14, H.ParseDecimal('3.14'), 'ParseDecimal(''3.14'') must return 3.14');
    end;

    [Test]
    procedure Evaluate_Decimal_WholeNumber()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.AreEqual(100, H.ParseDecimal('100'), 'ParseDecimal(''100'') must return 100');
    end;

    // ------------------------------------------------------------------
    // Positive: Return value — Evaluate returns true on success.
    // ------------------------------------------------------------------

    [Test]
    procedure Evaluate_ReturnTrue_OnSuccess()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.IsTrue(H.TryParseInteger('99'), 'TryParseInteger on valid text must return true');
    end;

    [Test]
    procedure Evaluate_ReturnFalse_OnFailure()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.IsFalse(H.TryParseInteger('not-a-number'), 'TryParseInteger on invalid text must return false');
    end;

    [Test]
    procedure Evaluate_ReturnFalse_EmptyString()
    var
        H: Codeunit "EVL Helper";
    begin
        Assert.IsFalse(H.TryParseInteger(''), 'TryParseInteger on empty text must return false');
    end;

    // ------------------------------------------------------------------
    // Negative: Invalid text causes an error when result is used.
    // ------------------------------------------------------------------

    [Test]
    procedure Evaluate_InvalidInteger_Errors()
    var
        H: Codeunit "EVL Helper";
    begin
        // ParseInteger calls Error() when Evaluate returns false
        asserterror H.ParseInteger('not-a-number');
        Assert.ExpectedError('Evaluate failed for integer');
    end;

    [Test]
    procedure Evaluate_InvalidDecimal_Errors()
    var
        H: Codeunit "EVL Helper";
    begin
        asserterror H.ParseDecimal('xyz');
        Assert.ExpectedError('Evaluate failed for decimal');
    end;
}
