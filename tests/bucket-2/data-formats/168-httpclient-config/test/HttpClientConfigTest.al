codeunit 94001 "HCC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HCC Src";

    // ── SetBaseAddress / GetBaseAddress ──────────────────────────

    [Test]
    procedure HttpClient_SetAndGetBaseAddress_RoundTrips()
    begin
        Assert.AreEqual('https://api.example.com',
            Src.SetAndGetBaseAddress('https://api.example.com'),
            'GetBaseAddress must return the URL set via SetBaseAddress');
    end;

    [Test]
    procedure HttpClient_SetBaseAddress_DifferentValues_Distinguishable()
    begin
        Assert.AreNotEqual(
            Src.SetAndGetBaseAddress('https://a.example.com'),
            Src.SetAndGetBaseAddress('https://b.example.com'),
            'Different base URLs must be stored independently');
    end;

    // ── Clear ────────────────────────────────────────────────────

    [Test]
    procedure HttpClient_Clear_ResetsBaseAddress()
    begin
        Assert.AreEqual('',
            Src.ClearResetsBaseAddress('https://api.example.com'),
            'Clear must reset base address to empty string');
    end;

    [Test]
    procedure HttpClient_GlobalClear_ResetsBaseAddress()
    begin
        // Clear(client) lowers to ALSystemVariable.Clear(client) → client.Clear()
        // MockHttpClient must expose a public Clear() method — issue #1334
        Assert.AreEqual('',
            Src.GlobalClearResetsBaseAddress('https://api.example.com'),
            'Global Clear(client) must reset base address to empty string');
    end;

    // ── UseResponseCookies ───────────────────────────────────────

    [Test]
    procedure HttpClient_UseResponseCookies_TrueDoesNotThrow()
    begin
        Assert.IsTrue(Src.UseResponseCookiesDoesNotThrow(true),
            'UseResponseCookies(true) must not throw');
    end;

    [Test]
    procedure HttpClient_UseResponseCookies_FalseDoesNotThrow()
    begin
        Assert.IsTrue(Src.UseResponseCookiesDoesNotThrow(false),
            'UseResponseCookies(false) must not throw');
    end;

    // ── UseServerCertificateValidation ───────────────────────────

    [Test]
    procedure HttpClient_UseServerCertificateValidation_TrueDoesNotThrow()
    begin
        Assert.IsTrue(Src.UseServerCertificateValidationDoesNotThrow(true),
            'UseServerCertificateValidation(true) must not throw');
    end;

    [Test]
    procedure HttpClient_UseServerCertificateValidation_FalseDoesNotThrow()
    begin
        Assert.IsTrue(Src.UseServerCertificateValidationDoesNotThrow(false),
            'UseServerCertificateValidation(false) must not throw');
    end;

    // ── UseWindowsAuthentication ─────────────────────────────────

    [Test]
    procedure HttpClient_UseWindowsAuthentication_TextOverloadDoesNotThrow()
    begin
        Assert.IsTrue(Src.UseWindowsAuthenticationText('user', 'pass'),
            'UseWindowsAuthentication(username, password) must not throw');
    end;

    [Test]
    procedure HttpClient_UseWindowsAuthentication_TextDomainOverloadDoesNotThrow()
    begin
        Assert.IsTrue(Src.UseWindowsAuthenticationTextDomain('user', 'pass', 'domain'),
            'UseWindowsAuthentication(username, password, domain) must not throw');
    end;

    // ── AddCertificate ───────────────────────────────────────────

    [Test]
    procedure HttpClient_AddCertificate_ThumbprintDoesNotThrow()
    begin
        Assert.IsTrue(Src.AddCertificateByThumbprint('AABBCCDD'),
            'AddCertificate(thumbprint) must not throw');
    end;

    [Test]
    procedure HttpClient_AddCertificate_ThumbprintAndPasswordDoesNotThrow()
    begin
        Assert.IsTrue(Src.AddCertificateByThumbprintAndPassword('AABBCCDD', 'secret'),
            'AddCertificate(thumbprint, password) must not throw');
    end;
}
