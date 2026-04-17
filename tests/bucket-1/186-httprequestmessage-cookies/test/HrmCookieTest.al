/// Tests for HttpRequestMessage cookie and secret URI methods — issue #738.
codeunit 122001 "HRMC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── SetCookie / GetCookie ─────────────────────────────────────

    [Test]
    procedure SetCookie_GetCookie_RoundTrips()
    var
        Req: HttpRequestMessage;
        Cookie: Cookie;
    begin
        Req.SetCookie('session', 'abc123');
        Assert.IsTrue(Req.GetCookie('session', Cookie),
            'GetCookie must return true for a cookie that was set');
        Assert.AreEqual('session', Cookie.Name,
            'Cookie name must match');
        Assert.AreEqual('abc123', Cookie.Value,
            'Cookie value must match');
    end;

    [Test]
    procedure GetCookie_MissingName_ReturnsFalse()
    var
        Req: HttpRequestMessage;
        Cookie: Cookie;
    begin
        Assert.IsFalse(Req.GetCookie('nonexistent', Cookie),
            'GetCookie must return false for a cookie that was never set');
    end;

    [Test]
    procedure SetCookie_TwoCookies_BothRetrievable()
    var
        Req: HttpRequestMessage;
        C1: Cookie;
        C2: Cookie;
    begin
        Req.SetCookie('user', 'alice');
        Req.SetCookie('token', 'xyz');
        Assert.IsTrue(Req.GetCookie('user', C1), 'user cookie must be found');
        Assert.IsTrue(Req.GetCookie('token', C2), 'token cookie must be found');
        Assert.AreEqual('alice', C1.Value, 'user value must match');
        Assert.AreEqual('xyz', C2.Value, 'token value must match');
    end;

    // ── RemoveCookie ──────────────────────────────────────────────

    [Test]
    procedure RemoveCookie_AfterSet_ReturnsFalseOnGet()
    var
        Req: HttpRequestMessage;
        Cookie: Cookie;
    begin
        Req.SetCookie('temp', 'data');
        Assert.IsTrue(Req.GetCookie('temp', Cookie), 'cookie must exist before removal');
        Req.RemoveCookie('temp');
        Assert.IsFalse(Req.GetCookie('temp', Cookie),
            'GetCookie must return false after RemoveCookie');
    end;

    // ── GetCookieNames ────────────────────────────────────────────

    [Test]
    procedure GetCookieNames_ReturnsAllSetNames()
    var
        Req: HttpRequestMessage;
        Names: List of [Text];
    begin
        Req.SetCookie('a', '1');
        Req.SetCookie('b', '2');
        Names := Req.GetCookieNames();
        Assert.AreEqual(2, Names.Count(), 'GetCookieNames must return 2 entries');
        Assert.IsTrue(Names.Contains('a'), 'names list must contain "a"');
        Assert.IsTrue(Names.Contains('b'), 'names list must contain "b"');
    end;

    [Test]
    procedure GetCookieNames_Empty_ReturnsEmptyList()
    var
        Req: HttpRequestMessage;
        Names: List of [Text];
    begin
        Names := Req.GetCookieNames();
        Assert.AreEqual(0, Names.Count(), 'GetCookieNames on fresh request must return empty list');
    end;
}
