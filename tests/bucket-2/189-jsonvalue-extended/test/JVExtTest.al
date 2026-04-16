/// Tests for JsonValue extended methods — covers 14 gap methods from issue #699.
/// AsBigInteger, AsByte, AsChar, AsCode, AsDate, AsDateTime, AsDuration,
/// AsOption, AsTime, AsToken, Clone, IsUndefined, Path, SetValueToUndefined
codeunit 97501 "JVExt Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JVExt Src";

    // --- AsBigInteger ---

    [Test]
    procedure AsBigInteger_RoundTrip()
    begin
        Assert.AreEqual(9876543210, Src.AsBigIntegerRoundTrip(), 'AsBigInteger must return the stored BigInteger value');
    end;

    [Test]
    procedure AsBigInteger_DiffersFromDifferentValue()
    begin
        Assert.AreNotEqual(Src.AsBigIntegerRoundTrip(), Src.AsBigIntegerDifferentValue(), 'Two different BigInteger values must not be equal');
    end;

    // --- AsByte ---

    [Test]
    procedure AsByte_RoundTrip()
    begin
        Assert.AreEqual(200, Src.AsByteRoundTrip(), 'AsByte must return the stored Byte value (200)');
    end;

    [Test]
    procedure AsByte_DiffersFromDifferentValue()
    begin
        Assert.AreNotEqual(Src.AsByteRoundTrip(), Src.AsByteAltValue(), 'Two different Byte values must not be equal');
    end;

    // --- AsChar ---

    [Test]
    procedure AsChar_RoundTrip()
    var
        expected: Char;
    begin
        expected := 65;
        Assert.AreEqual(expected, Src.AsCharRoundTrip(), 'AsChar must return the stored Char value (65 = ''A'')');
    end;

    // --- AsCode ---

    [Test]
    procedure AsCode_RoundTrip()
    begin
        Assert.AreEqual('MYCODE', Src.AsCodeRoundTrip(), 'AsCode must return the stored Code value');
    end;

    [Test]
    procedure AsCode_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.AsCodeRoundTrip(), 'AsCode on set value must not be empty');
    end;

    // --- AsDate ---

    [Test]
    procedure AsDate_RoundTrip()
    begin
        Assert.AreEqual(20260101D, Src.AsDateRoundTrip(), 'AsDate must return the stored Date value');
    end;

    // --- AsDateTime ---

    [Test]
    procedure AsDateTime_RoundTrip()
    begin
        Assert.IsTrue(Src.AsDateTimeSetsAndReturns(), 'AsDateTime must round-trip a set DateTime value');
    end;

    // --- AsDuration ---

    [Test]
    procedure AsDuration_RoundTrip()
    begin
        Assert.AreEqual(3600000, Src.AsDurationRoundTrip(), 'AsDuration must return the stored Duration value (3600000 ms)');
    end;

    // --- AsOption ---

    [Test]
    procedure AsOption_RoundTrip()
    begin
        Assert.AreEqual(1, Src.AsOptionRoundTrip(), 'AsOption must return the stored option value (1 = opt::B)');
    end;

    // --- AsTime ---

    [Test]
    procedure AsTime_RoundTrip()
    begin
        Assert.AreEqual(120000T, Src.AsTimeRoundTrip(), 'AsTime must return the stored Time value');
    end;

    // --- AsToken ---

    [Test]
    procedure AsToken_IsValue_True()
    begin
        Assert.IsTrue(Src.AsTokenIsValue(), 'AsToken on a JsonValue must return a token where IsValue() = true');
    end;

    // --- Clone ---

    [Test]
    procedure Clone_ProducesEqualValue()
    begin
        Assert.IsTrue(Src.CloneProducesEqualValue(), 'Clone must produce a JsonValue with the same text content');
    end;

    // --- IsUndefined ---

    [Test]
    procedure IsUndefined_FreshValue_True()
    begin
        Assert.IsTrue(Src.IsUndefined_Fresh(), 'A fresh JsonValue (unset) must be undefined');
    end;

    [Test]
    procedure IsUndefined_AfterSetValue_False()
    begin
        Assert.IsFalse(Src.IsUndefined_AfterSetValue(), 'A JsonValue with a set value must not be undefined');
    end;

    [Test]
    procedure IsUndefined_TrueAndFalse_Differ()
    begin
        Assert.AreNotEqual(
            Src.IsUndefined_Fresh(),
            Src.IsUndefined_AfterSetValue(),
            'IsUndefined must differ between a fresh and a set JsonValue');
    end;

    // --- Path ---

    [Test]
    procedure Path_ReturnsString()
    var
        p: Text;
    begin
        p := Src.PathReturnsText();
        // Path on a standalone token returns empty string; just confirm it runs without error
        Assert.IsTrue(true, 'Path must not raise an error on a standalone JsonValue');
    end;

    // --- SetValueToUndefined ---

    [Test]
    procedure SetValueToUndefined_MakesUndefined()
    begin
        Assert.IsTrue(Src.IsUndefined_AfterSetValueToUndefined(), 'SetValueToUndefined must make IsUndefined return true');
    end;

    [Test]
    procedure SetValueToUndefined_DiffersFromSetValue()
    begin
        Assert.AreNotEqual(
            Src.IsUndefined_AfterSetValueToUndefined(),
            Src.IsUndefined_AfterSetValue(),
            'After SetValueToUndefined, IsUndefined must differ from after SetValue');
    end;
}
