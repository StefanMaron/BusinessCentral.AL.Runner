codeunit 307902 "PAF Page AutoFormat Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageWithAutoFormatFieldCompiles()
    var
        Helper: Codeunit "PAF Helper";
        Result: Decimal;
    begin
        // [GIVEN] A page with a field that has AutoFormatType = 1 (triggers
        //         CallGetAutoFormatStringExtensionMethod + EnsureGlobalVariablesInitialized
        //         on the Page<N> class) and a trigger that calls ShowCalculatingText()
        // [WHEN]  The page source is compiled and the helper logic runs
        Result := Helper.InsertAndGetAmount(307900, 42.5);

        // [THEN] The value round-trips correctly — proves the page compiled and the
        //        record layer works; a no-op stub would return 0.0
        Assert.AreEqual(42.5, Result, 'Amount should round-trip through the record');
    end;

    [Test]
    procedure PageWithAutoFormatFieldNonZeroValue()
    var
        Helper: Codeunit "PAF Helper";
        Result: Decimal;
    begin
        // [NEGATIVE] A different amount must not equal the first test's amount
        Result := Helper.InsertAndGetAmount(307901, 99.99);
        Assert.AreNotEqual(42.5, Result, 'Second record amount must differ from first');
        Assert.AreEqual(99.99, Result, 'Second record amount should be 99.99');
    end;
}
