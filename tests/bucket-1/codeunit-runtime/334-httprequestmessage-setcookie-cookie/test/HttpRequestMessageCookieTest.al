codeunit 1320503 "HRM Cookie Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HRM Cookie Src";

    [Test]
    procedure SetCookieObject_ReturnsTrue()
    begin
        Assert.IsTrue(Src.SetCookieObjectReturnsTrue(),
            'HttpRequestMessage.SetCookie(Cookie) should return true');
    end;

    [Test]
    procedure SetCookieObject_RoundTripValue()
    begin
        Assert.AreEqual('abc-123', Src.SetCookieObjectRoundTrip(),
            'SetCookie(Cookie) should allow GetCookie to return the stored value');
    end;

    [Test]
    procedure GetCookie_Missing_ReturnsFalse()
    begin
        Assert.IsFalse(Src.GetCookieMissingReturnsFalse(),
            'GetCookie should return false when the cookie does not exist');
    end;
}
