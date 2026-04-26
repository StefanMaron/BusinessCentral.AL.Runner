codeunit 81301 "EICN Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EICN Src";

    // -----------------------------------------------------------------------
    // Positive: ControlName round-trip
    // -----------------------------------------------------------------------

    [Test]
    procedure ControlName_SetAndGet_ReturnsSetValue()
    begin
        // Positive: ControlName() returns the value set by ControlName(value)
        Assert.AreEqual('MyField', Src.SetAndGet(),
            'ErrorInfo.ControlName round-trip must return the value that was set');
    end;

    [Test]
    procedure ControlName_Overwrite_ReturnsLatest()
    begin
        // Positive: setting ControlName twice returns the second value
        Assert.AreEqual('Second', Src.OverwriteControlName(),
            'ControlName must return the last value set when set twice');
    end;

    // -----------------------------------------------------------------------
    // Negative: default value
    // -----------------------------------------------------------------------

    [Test]
    procedure ControlName_Default_IsEmpty()
    begin
        // Negative: fresh ErrorInfo has empty ControlName
        Assert.AreEqual('', Src.DefaultControlName(),
            'Default ErrorInfo.ControlName must be empty');
    end;

    [Test]
    procedure ControlName_Default_NotMyField()
    var
        EI: ErrorInfo;
    begin
        // Negative: fresh ErrorInfo ControlName is not 'MyField'
        Assert.AreNotEqual('MyField', EI.ControlName(),
            'Default ErrorInfo.ControlName must not be ''MyField''');
    end;

    // -----------------------------------------------------------------------
    // Positive: set to empty explicitly
    // -----------------------------------------------------------------------

    [Test]
    procedure ControlName_SetEmpty_ReturnsEmpty()
    begin
        // Positive: setting ControlName to '' returns ''
        Assert.AreEqual('', Src.SetEmpty(),
            'ErrorInfo.ControlName set to empty string must return empty string');
    end;

    // -----------------------------------------------------------------------
    // Positive: direct inline usage
    // -----------------------------------------------------------------------

    [Test]
    procedure ControlName_InlineSetGet()
    var
        EI: ErrorInfo;
    begin
        // Positive: inline set/get without helper codeunit
        EI.ControlName('QuantityField');
        Assert.AreEqual('QuantityField', EI.ControlName(),
            'Inline ControlName round-trip must return set value');
    end;
}
