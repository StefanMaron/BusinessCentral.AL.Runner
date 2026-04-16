codeunit 105001 "SEVS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SEVS Src";

    [Test]
    procedure GuiAllowed_FalseInRunner()
    begin
        Assert.IsFalse(Src.IsGui(), 'GuiAllowed must be false in standalone runner');
    end;

    [Test]
    procedure IsServiceTier_FalseInRunner()
    begin
        Assert.IsFalse(Src.IsSvcTier(), 'IsServiceTier must be false in standalone runner');
    end;

    [Test]
    procedure ApplicationPath_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.GetApplicationPath(), 'ApplicationPath must return a non-empty path');
    end;

    [Test]
    procedure TemporaryPath_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.GetTemporaryPath(), 'TemporaryPath must return a non-empty path');
    end;

    [Test]
    procedure WorkDate_GetSet_RoundTrips()
    begin
        Src.SetWorkDate(20260417D);
        Assert.AreEqual(20260417D, Src.GetWorkDate(), 'WorkDate get/set must round-trip');
    end;

    [Test]
    procedure GlobalLanguage_DefaultNonZero()
    begin
        Assert.AreNotEqual(0, Src.GetGlobalLang(), 'GlobalLanguage must return non-zero LCID');
    end;

    [Test]
    procedure GlobalLanguage_SetAndGet()
    begin
        Src.SetGlobalLang(2052); // Chinese Simplified
        Assert.AreEqual(2052, Src.GetGlobalLang(), 'GlobalLanguage must return the value that was set');
    end;

    [Test]
    procedure WindowsLanguage_NonZero()
    begin
        Assert.AreNotEqual(0, Src.GetWindowsLang(), 'WindowsLanguage must return non-zero LCID');
    end;

    [Test]
    procedure RoundDateTime_ToHour_RoundsToNearestHour()
    var
        DT: DateTime;
        Rounded: DateTime;
    begin
        // 08:10 is 10 minutes into hour — nearest hour boundary is 08:00 (10 min away vs 50 min to 09:00)
        DT := CreateDateTime(20260417D, 081000T);
        Rounded := Src.RoundDT(DT, 3600000); // 1h in ms
        Assert.AreEqual(CreateDateTime(20260417D, 080000T), Rounded, 'RoundDateTime(08:10, 1h) must round to 08:00');
    end;

    [Test]
    procedure IsNull_FalseForText()
    var
        V: Variant;
    begin
        V := 'hello';
        Assert.IsFalse(Src.NullCheck(V), 'IsNull must return false for a non-null text variant');
    end;

    [Test]
    procedure Hyperlink_NoThrow()
    begin
        // no-op in runner — must not throw
        Src.OpenHyperlink('https://example.com');
    end;
}
