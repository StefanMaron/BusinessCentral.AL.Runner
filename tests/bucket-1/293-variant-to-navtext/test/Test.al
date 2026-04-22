/// Tests for Variant → Text assignment via NavIndirectValueToNavValue<NavText>.
/// Before the fix the runner emitted:
///   AlCompat.NavIndirectValueToNavValue<NavText>(v, metadata)
/// which fails compilation (CS1501 – no overload takes 2 arguments).
codeunit 293002 "VNT Variant NavText Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ---------------------------------------------------------------
    // Positive: Boolean wrapped in Variant assigned to Text
    // The BC transpiler emits NavIndirectValueToNavValue<NavText>(v, meta).
    // Before the fix this crashed at Roslyn compilation.
    // ---------------------------------------------------------------
    [Test]
    procedure BoolVariantToText_True_YieldsYes()
    var
        Helper: Codeunit "VNT Variant NavText Helper";
    begin
        // [GIVEN/WHEN] true Boolean → Variant → Text
        Assert.AreEqual('Yes', Helper.BoolVariantToText(true), 'true Boolean via Variant to Text should be Yes');
    end;

    [Test]
    procedure BoolVariantToText_False_YieldsNo()
    var
        Helper: Codeunit "VNT Variant NavText Helper";
    begin
        Assert.AreEqual('No', Helper.BoolVariantToText(false), 'false Boolean via Variant to Text should be No');
    end;

    // ---------------------------------------------------------------
    // Negative: result must not be empty (catches no-op stub)
    // ---------------------------------------------------------------
    [Test]
    procedure BoolVariantToText_True_IsNotEmpty()
    var
        Helper: Codeunit "VNT Variant NavText Helper";
    begin
        Assert.AreNotEqual('', Helper.BoolVariantToText(true), 'Boolean via Variant to Text must not be empty');
    end;

    // ---------------------------------------------------------------
    // Integer via Variant to Text
    // ---------------------------------------------------------------
    [Test]
    procedure IntVariantToText_42_Yields42()
    var
        Helper: Codeunit "VNT Variant NavText Helper";
    begin
        Assert.AreEqual('42', Helper.IntVariantToText(42), 'Integer 42 via Variant to Text should be 42');
    end;

    [Test]
    procedure IntVariantToText_Zero_YieldsZero()
    var
        Helper: Codeunit "VNT Variant NavText Helper";
    begin
        Assert.AreEqual('0', Helper.IntVariantToText(0), 'Integer 0 via Variant to Text should be 0');
    end;

    // ---------------------------------------------------------------
    // Text round-trip via Variant
    // ---------------------------------------------------------------
    [Test]
    procedure TextVariantToText_RoundTrip()
    var
        Helper: Codeunit "VNT Variant NavText Helper";
    begin
        Assert.AreEqual('hello', Helper.TextVariantToText('hello'), 'Text round-trip via Variant should preserve value');
    end;
}
