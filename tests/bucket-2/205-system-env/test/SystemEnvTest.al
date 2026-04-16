codeunit 60331 "SEV Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SEV Src";

    [Test]
    procedure GuiAllowed_ReturnsFalse()
    begin
        // Standalone contract: no GUI, so GuiAllowed is false.
        Assert.IsFalse(Src.IsGui(),
            'GuiAllowed must return false in standalone mode');
    end;

    [Test]
    procedure WorkDate_ReturnsNonZero()
    begin
        // WorkDate defaults to Today() which is non-zero.
        Assert.AreNotEqual(0D, Src.GetWorkDate(),
            'WorkDate must return a non-zero date');
    end;

    [Test]
    procedure WorkDate_SetAndGet_RoundTrips()
    begin
        Assert.AreEqual(DMY2Date(1, 1, 2025), Src.SetAndGetWorkDate(DMY2Date(1, 1, 2025)),
            'WorkDate setter + getter must round-trip');
    end;

    [Test]
    procedure GlobalLanguage_ReturnsPositive()
    begin
        // Should return a Windows LCID (e.g. 1033 for en-US).
        Assert.IsTrue(Src.GetGlobalLang() > 0,
            'GlobalLanguage must return a positive LCID');
    end;

    [Test]
    procedure WindowsLanguage_ReturnsPositive()
    begin
        Assert.IsTrue(Src.GetWindowsLang() > 0,
            'WindowsLanguage must return a positive LCID');
    end;

    [Test]
    procedure RoundDateTime_DoesNotThrow()
    begin
        // Just verify it completes — the detailed rounding is tested elsewhere.
        Src.RoundDateTimePrecision(CurrentDateTime(), 1000);
        Assert.IsTrue(true, 'RoundDateTime must not throw');
    end;
}
