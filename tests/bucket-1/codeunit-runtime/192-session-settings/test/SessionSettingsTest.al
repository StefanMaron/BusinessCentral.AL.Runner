codeunit 60181 "SST Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SST Src";

    [Test]
    procedure Init_DoesNotThrow()
    begin
        Assert.IsTrue(Src.Init_DoesNotThrow(),
            'SessionSettings.Init must complete without throwing');
    end;

    [Test]
    procedure DefaultCompany_IsEmpty()
    begin
        Assert.AreEqual('', Src.GetCompany(),
            'Default SessionSettings.Company must be empty');
    end;

    [Test]
    procedure SetAndGet_Company()
    begin
        Assert.AreEqual('Contoso', Src.SetAndGetCompany('Contoso'),
            'SessionSettings.Company setter + getter must round-trip');
    end;

    [Test]
    procedure SetAndGet_LanguageId()
    begin
        Assert.AreEqual(1033, Src.SetAndGetLanguageId(1033),
            'SessionSettings.LanguageId setter + getter must round-trip');
    end;

    [Test]
    procedure SetAndGet_LocaleId()
    begin
        Assert.AreEqual(2057, Src.SetAndGetLocaleId(2057),
            'SessionSettings.LocaleId setter + getter must round-trip');
    end;

    [Test]
    procedure SetAndGet_TimeZone()
    begin
        Assert.AreEqual('UTC', Src.SetAndGetTimeZone('UTC'),
            'SessionSettings.TimeZone setter + getter must round-trip');
    end;

    [Test]
    procedure SetAndGet_ProfileId()
    begin
        Assert.AreEqual('ACCOUNTANT', Src.SetAndGetProfileId('ACCOUNTANT'),
            'SessionSettings.ProfileId setter + getter must round-trip');
    end;

    [Test]
    procedure RequestSessionUpdate_Is_NoOp()
    begin
        Assert.IsTrue(Src.RequestSessionUpdate_NoOp(),
            'RequestSessionUpdate must be a standalone no-op that preserves local state');
    end;

    [Test]
    procedure Company_Setter_NotANoop_NegativeTrap()
    begin
        // Negative trap: make sure the setter actually stores — if it were a
        // no-op the result would equal the default empty string.
        Assert.AreNotEqual('', Src.SetAndGetCompany('Contoso'),
            'Company setter must not be a no-op — value must persist');
    end;

    [Test]
    procedure ProfileAppId_DefaultsToEmptyGuid()
    begin
        Assert.IsTrue(IsNullGuid(Src.GetProfileAppId()),
            'Default SessionSettings.ProfileAppId must be the empty GUID');
    end;

    [Test]
    procedure SetAndGet_ProfileAppId()
    var
        g: Guid;
    begin
        g := '{12345678-1234-1234-1234-1234567890AB}';
        Assert.AreEqual(g, Src.SetAndGetProfileAppId(g),
            'SessionSettings.ProfileAppId setter + getter must round-trip');
    end;

    [Test]
    procedure ProfileAppId_Setter_NotANoop()
    var
        g: Guid;
    begin
        g := '{12345678-1234-1234-1234-1234567890AB}';
        Assert.IsFalse(IsNullGuid(Src.SetAndGetProfileAppId(g)),
            'ProfileAppId setter must not be a no-op — value must differ from empty GUID');
    end;

    [Test]
    procedure ProfileSystemScope_DefaultsToFalse()
    begin
        Assert.IsFalse(Src.GetProfileSystemScope(),
            'Default SessionSettings.ProfileSystemScope must be false');
    end;

    [Test]
    procedure SetAndGet_ProfileSystemScope()
    begin
        Assert.IsTrue(Src.SetAndGetProfileSystemScope(true),
            'SessionSettings.ProfileSystemScope setter + getter must round-trip');
    end;

    [Test]
    procedure ProfileSystemScope_Setter_NotANoop()
    begin
        Assert.AreNotEqual(false, Src.SetAndGetProfileSystemScope(true),
            'ProfileSystemScope setter must not be a no-op — true must not equal default false');
    end;
}
