// Test suite for issue #1184: MockVariant to NavIndirectValue conversion.
// When a Variant containing a codeunit is assigned back to a codeunit variable,
// BC emits ALCompiler.NavIndirectValueToNavCodeunitHandle(variant), which the
// rewriter must convert to (MockCodeunitHandle)(variant) — mirroring the treatment
// of NavIndirectValueToINavRecordHandle.
codeunit 233003 "VCU Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure DispatchViaVariant_Doubler_DoublesValue()
    var
        Dispatcher: Codeunit "VCU Dispatcher";
        Result: Integer;
    begin
        // [GIVEN] Doubler codeunit stored in Variant then extracted
        // [WHEN] Dispatch with UseDoubler=true
        // [THEN] Result is Value * 2
        Result := Dispatcher.DispatchViaVariant(true, 7);
        Assert.AreEqual(14, Result, 'Doubler extracted from Variant must return 7*2=14');
    end;

    [Test]
    procedure DispatchViaVariant_Tripler_TriplesValue()
    var
        Dispatcher: Codeunit "VCU Dispatcher";
        Result: Integer;
    begin
        // [GIVEN] Tripler codeunit stored in Variant then extracted
        // [WHEN] Dispatch with UseDoubler=false
        // [THEN] Result is Value * 3
        Result := Dispatcher.DispatchViaVariant(false, 5);
        Assert.AreEqual(15, Result, 'Tripler extracted from Variant must return 5*3=15');
    end;

    [Test]
    procedure IsCodeunitInVariant_ReturnsTrue()
    var
        Dispatcher: Codeunit "VCU Dispatcher";
    begin
        // [GIVEN] A codeunit stored in a Variant
        // [WHEN] IsCodeunit() is checked
        // [THEN] Returns true
        Assert.IsTrue(Dispatcher.IsCodeunitInVariant(),
            'Variant holding a codeunit must report IsCodeunit = true');
    end;

    [Test]
    procedure DispatchViaVariant_Negative_TriplerNotDoubler()
    var
        Dispatcher: Codeunit "VCU Dispatcher";
        Result: Integer;
    begin
        // [NEGATIVE] Tripler should NOT return same as Doubler for same input
        Result := Dispatcher.DispatchViaVariant(false, 5);
        Assert.AreNotEqual(10, Result, 'Tripler (15) must not equal Doubler (10) for value 5');
    end;
}
