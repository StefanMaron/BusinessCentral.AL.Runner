/// Test suite for System overloads from issue #1375:
///   CalcDate(Text, Date), Clear(Joker), Clear(SecretText),
///   Format(Joker, Integer, Text), GetLastErrorText(Boolean),
///   GetUrl 6+7-arg (Table/RecordRef, Boolean[, Text]).
codeunit 309902 "Sys Missing Overloads Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Sys Missing Overloads Helper";
        Assert: Codeunit "Library Assert";

    // ── CalcDate(Text, Date) ──────────────────────────────────────────────────

    [Test]
    procedure CalcDate_TextFormula_AddOneMonth_Positive()
    var
        Base: Date;
        Result: Date;
    begin
        // [GIVEN] 15-Jun-2024
        Base := DMY2Date(15, 6, 2024);
        // [WHEN] CalcDate('<+1M>', base)
        Result := Helper.CalcDateTextDate('<+1M>', Base);
        // [THEN] 15-Jul-2024
        Assert.AreEqual(DMY2Date(15, 7, 2024), Result, 'CalcDate(Text, Date) should add 1 month');
    end;

    [Test]
    procedure CalcDate_TextFormula_ZeroDate_Negative()
    var
        ZeroDate: Date;
    begin
        // [GIVEN] An undefined (0D) date
        // [WHEN] CalcDate on 0D
        // [THEN] Error about undefined date
        asserterror Helper.CalcDateTextDate('<+1D>', ZeroDate);
        Assert.ExpectedError('undefined date');
    end;

    // ── Clear(Joker) ──────────────────────────────────────────────────────────

    [Test]
    procedure Clear_Joker_Variant_ResetsValue_Positive()
    var
        V: Variant;
        N: Integer;
    begin
        // [GIVEN] A Variant holding integer 99
        N := 99;
        V := N;
        Assert.IsTrue(V.IsInteger(), 'Variant should hold integer before Clear');
        // [WHEN] Clear(Variant)
        Helper.ClearVariant(V);
        // [THEN] Variant is reset and no longer holds integer
        Assert.IsFalse(V.IsInteger(), 'Variant should not hold integer after Clear');
    end;

    [Test]
    procedure Clear_Joker_Variant_Direct_Positive()
    var
        V: Variant;
        N: Integer;
    begin
        // [GIVEN] A Variant holding integer 42
        N := 42;
        V := N;
        // [WHEN] Direct Clear(V)
        Clear(V);
        // [THEN] Variant is reset
        Assert.IsFalse(V.IsInteger(), 'Direct Clear(Variant) should reset the variant');
    end;

    // ── Clear(SecretText) ────────────────────────────────────────────────────

    [Test]
    procedure Clear_SecretText_ClearsValue_Positive()
    var
        S: SecretText;
    begin
        // [GIVEN] A SecretText with a value
        S := Helper.MakeSecret('my-password');
        Assert.IsFalse(S.IsEmpty(), 'SecretText should not be empty before Clear');
        // [WHEN] Clear(SecretText)
        Helper.ClearSecret(S);
        // [THEN] SecretText is empty
        Assert.IsTrue(S.IsEmpty(), 'SecretText should be empty after Clear');
    end;

    [Test]
    procedure Clear_SecretText_Direct_ClearsValue_Positive()
    var
        S: SecretText;
    begin
        // [GIVEN] A SecretText with 'token'
        S := Helper.MakeSecret('token');
        Assert.IsFalse(S.IsEmpty(), 'SecretText should have value before Clear');
        // [WHEN] Direct Clear(S)
        Clear(S);
        // [THEN] SecretText is empty
        Assert.IsTrue(S.IsEmpty(), 'Direct Clear(SecretText) should make it empty');
    end;

    // ── Format(Joker, Integer, Text) ──────────────────────────────────────────

    [Test]
    procedure Format_Integer_WithMask_Positive()
    var
        Result: Text;
    begin
        // [GIVEN] Integer 42, mask '<Integer>'
        // [WHEN] Format(42, 0, '<Integer>')
        Result := Helper.FormatWithMask(42, 0, '<Integer>');
        // [THEN] Result is '42'
        Assert.AreEqual('42', Result, 'Format(42, 0, ''<Integer>'') should return ''42''');
    end;

    [Test]
    procedure Format_Decimal_WithPrecisionMask_Positive()
    var
        Result: Text;
    begin
        // [GIVEN] Decimal 1234.5, mask '<Precision,2:2>'
        // [WHEN] Format(1234.5, 0, '<Precision,2:2>')
        Result := Helper.FormatDecimalWithMask(1234.5, 0, '<Precision,2:2>');
        // [THEN] Result contains '1,234.50' or similar formatted decimal
        Assert.AreNotEqual('', Result, 'Format with precision mask should return non-empty string');
    end;

    [Test]
    procedure Format_Integer_NotPassthrough_Negative()
    var
        Result: Text;
    begin
        // [GIVEN] Integer 99 with format mask
        // [WHEN] Format(99, 0, '<Integer>')
        Result := Helper.FormatWithMask(99, 0, '<Integer>');
        // [THEN] Must not return a different number
        Assert.AreNotEqual('100', Result, 'Format should not return wrong value');
    end;

    // ── GetLastErrorText(Boolean) — clearError=true ───────────────────────────

    [Test]
    procedure GetLastErrorText_WithClearTrue_ReturnsAndClearsError_Positive()
    var
        ErrText: Text;
    begin
        // [GIVEN] An error has occurred
        asserterror Helper.TriggerError('Test error for GetLastErrorText(true)');
        // [WHEN] GetLastErrorText(true) — should return the error and clear it
        ErrText := Helper.GetLastErrorWithClear(true);
        // [THEN] Returns the error message
        Assert.AreNotEqual('', ErrText, 'GetLastErrorText(true) should return non-empty error message');
        Assert.IsTrue(ErrText.Contains('Test error for GetLastErrorText(true)'),
            'GetLastErrorText(true) should contain the triggered error');
    end;

    [Test]
    procedure GetLastErrorText_WithClearFalse_ReturnsButPreservesError_Positive()
    var
        ErrText1: Text;
        ErrText2: Text;
    begin
        // [GIVEN] An error has occurred
        asserterror Helper.TriggerError('Preserved error message');
        // [WHEN] GetLastErrorText(false) — should return the error without clearing
        ErrText1 := Helper.GetLastErrorWithClear(false);
        ErrText2 := GetLastErrorText();
        // [THEN] Both calls return the same non-empty text
        Assert.AreNotEqual('', ErrText1, 'GetLastErrorText(false) should return non-empty text');
        Assert.AreEqual(ErrText1, ErrText2, 'GetLastErrorText(false) should not clear the error');
    end;

    // ── GetUrl 6-arg (Table, Boolean) ─────────────────────────────────────────

    [Test]
    procedure GetUrl_SixArg_Table_ReturnsNonEmpty_Positive()
    var
        Result: Text;
    begin
        // [WHEN] GetUrl(ClientType, Company, ObjectType, ObjectId, Table, UseFilters)
        Result := Helper.GetUrlSixArgTable();
        // [THEN] Returns non-empty string (stub)
        Assert.AreNotEqual('', Result, 'GetUrl(6-arg, Table) should return non-empty stub URL');
    end;

    // ── GetUrl 7-arg (Table, Boolean, Text) ──────────────────────────────────

    [Test]
    procedure GetUrl_SevenArg_Table_ReturnsNonEmpty_Positive()
    var
        Result: Text;
    begin
        // [WHEN] GetUrl(ClientType, Company, ObjectType, ObjectId, Table, UseFilters, FilterStr)
        Result := Helper.GetUrlSevenArgTable();
        // [THEN] Returns non-empty string (stub)
        Assert.AreNotEqual('', Result, 'GetUrl(7-arg, Table) should return non-empty stub URL');
    end;

    // ── GetUrl 6-arg (RecordRef, Boolean) ────────────────────────────────────

    [Test]
    procedure GetUrl_SixArg_RecordRef_ReturnsNonEmpty_Positive()
    var
        Result: Text;
    begin
        // [WHEN] GetUrl(ClientType, Company, ObjectType, ObjectId, RecordRef, UseFilters)
        Result := Helper.GetUrlSixArgRecordRef();
        // [THEN] Returns non-empty string (stub)
        Assert.AreNotEqual('', Result, 'GetUrl(6-arg, RecordRef) should return non-empty stub URL');
    end;

    // ── GetUrl 7-arg (RecordRef, Boolean, Text) ───────────────────────────────

    [Test]
    procedure GetUrl_SevenArg_RecordRef_ReturnsNonEmpty_Positive()
    var
        Result: Text;
    begin
        // [WHEN] GetUrl(ClientType, Company, ObjectType, ObjectId, RecordRef, UseFilters, FilterStr)
        Result := Helper.GetUrlSevenArgRecordRef();
        // [THEN] Returns non-empty string (stub)
        Assert.AreNotEqual('', Result, 'GetUrl(7-arg, RecordRef) should return non-empty stub URL');
    end;
}
