codeunit 1320501 "HH Cookie Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HH Cookie Bool Src";

    [Test]
    procedure AddHeader_ReturnsTrue()
    begin
        Assert.IsTrue(Src.AddHeaderReturnsTrue(),
            'HttpHeaders.Add should return true when header is added');
    end;

    [Test]
    procedure SetCookie_ReturnsTrue()
    begin
        Assert.IsTrue(Src.SetCookieReturnsTrue(),
            'HttpRequestMessage.SetCookie(Text, Text) should return true');
    end;

    [Test]
    procedure RemoveCookie_ReturnsTrue()
    begin
        Assert.IsTrue(Src.RemoveCookieReturnsTrue(),
            'HttpRequestMessage.RemoveCookie should return true when cookie exists');
    end;

    [Test]
    procedure RemoveCookie_Missing_ReturnsFalse()
    begin
        Assert.IsFalse(Src.RemoveCookieMissingReturnsFalse(),
            'HttpRequestMessage.RemoveCookie should return false when cookie is missing');
    end;
}
