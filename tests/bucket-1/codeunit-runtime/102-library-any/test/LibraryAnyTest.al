// Tests for codeunit 130500 "Any" — pseudo-random test data generator.
// The stub is auto-loaded from AlRunner/stubs/LibraryAny.al.
codeunit 100003 "Library Any Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── IntegerInRange(MinValue, MaxValue) ────────────────────────────────────

    [Test]
    procedure IntegerInRange_ReturnsValueInBounds()
    var
        Any: Codeunit Any;
        Result: Integer;
    begin
        Result := Any.IntegerInRange(1, 100);
        Assert.IsTrue((Result >= 1) and (Result <= 100),
            'IntegerInRange(1, 100) must return value in [1, 100]');
    end;

    [Test]
    procedure IntegerInRange_LowBound_ReturnsThatValue()
    var
        Any: Codeunit Any;
        Result: Integer;
    begin
        Result := Any.IntegerInRange(5, 5);
        Assert.AreEqual(5, Result,
            'IntegerInRange(5, 5) must return exactly 5');
    end;

    [Test]
    procedure IntegerInRange_OneArg_ReturnsValueInBounds()
    var
        Any: Codeunit Any;
        Result: Integer;
    begin
        Result := Any.IntegerInRange(50);
        Assert.IsTrue((Result >= 1) and (Result <= 50),
            'IntegerInRange(50) must return value in [1, 50]');
    end;

    // ── BooleanValue (Boolean()) ──────────────────────────────────────────────

    [Test]
    procedure BooleanValue_NoThrow()
    var
        Any: Codeunit Any;
        Result: Boolean;
    begin
        // Just confirming it does not throw; any bool value is valid
        Result := Any.Boolean();
        Assert.IsTrue(true, 'Any.Boolean() must not throw');
    end;

    // ── GuidValue ─────────────────────────────────────────────────────────────

    [Test]
    procedure GuidValue_NonNull()
    var
        Any: Codeunit Any;
        G: Guid;
    begin
        G := Any.GuidValue();
        Assert.IsFalse(IsNullGuid(G), 'GuidValue must return a non-null GUID');
    end;

    [Test]
    procedure GuidValue_Unique()
    var
        Any: Codeunit Any;
        G1: Guid;
        G2: Guid;
    begin
        G1 := Any.GuidValue();
        G2 := Any.GuidValue();
        Assert.AreNotEqual(Format(G1), Format(G2),
            'Two GuidValue() calls must return different GUIDs');
    end;

    // ── AlphanumericText ──────────────────────────────────────────────────────

    [Test]
    procedure AlphanumericText_LengthInBounds()
    var
        Any: Codeunit Any;
        Result: Text;
    begin
        Result := Any.AlphanumericText(10);
        Assert.IsTrue((StrLen(Result) > 0) and (StrLen(Result) <= 10),
            'AlphanumericText(10) must return a non-empty text of length <= 10');
    end;

    [Test]
    procedure AlphanumericText_ExactLength()
    var
        Any: Codeunit Any;
        Result: Text;
    begin
        Result := Any.AlphanumericText(32);
        Assert.AreEqual(32, StrLen(Result),
            'AlphanumericText(32) must return exactly 32 characters');
    end;

    // ── DecimalInRange ────────────────────────────────────────────────────────

    [Test]
    procedure DecimalInRange_MaxValue_InBounds()
    var
        Any: Codeunit Any;
        Result: Decimal;
    begin
        Result := Any.DecimalInRange(100, 2);
        Assert.IsTrue((Result > 0) and (Result <= 100),
            'DecimalInRange(100, 2) must return value in (0, 100]');
    end;

    [Test]
    procedure DecimalInRange_MinMax_InBounds()
    var
        Any: Codeunit Any;
        Result: Decimal;
    begin
        Result := Any.DecimalInRange(10, 20, 2);
        Assert.IsTrue((Result >= 10) and (Result <= 20),
            'DecimalInRange(10, 20, 2) must return value in [10, 20]');
    end;

    // ── AlphabeticText ────────────────────────────────────────────────────────

    [Test]
    procedure AlphabeticText_ExactLength()
    var
        Any: Codeunit Any;
        Result: Text;
    begin
        Result := Any.AlphabeticText(8);
        Assert.AreEqual(8, StrLen(Result),
            'AlphabeticText(8) must return exactly 8 characters');
    end;

    // ── Email ─────────────────────────────────────────────────────────────────

    [Test]
    procedure Email_ContainsAtSign()
    var
        Any: Codeunit Any;
        Result: Text;
    begin
        Result := Any.Email();
        Assert.IsTrue(StrPos(Result, '@') > 0,
            'Email() must contain an @ sign');
    end;

    // ── SetSeed / GetSeed ─────────────────────────────────────────────────────

    [Test]
    procedure SetSeed_GetSeed_RoundTrip()
    var
        Any: Codeunit Any;
    begin
        Any.SetSeed(42);
        Assert.AreEqual(42, Any.GetSeed(), 'GetSeed must return the seed set by SetSeed');
    end;

    // ── DateInRange ───────────────────────────────────────────────────────────

    [Test]
    procedure DateInRange_StartingDate_InBounds()
    var
        Any: Codeunit Any;
        StartDate: Date;
        Result: Date;
    begin
        // Use an explicit date to avoid runner limitation with WorkDate() = 0D
        Evaluate(StartDate, '01/01/2024');
        Result := Any.DateInRange(StartDate, 1, 30);
        Assert.IsTrue(Result >= StartDate,
            'DateInRange with StartingDate must return a date >= StartingDate');
    end;
}
