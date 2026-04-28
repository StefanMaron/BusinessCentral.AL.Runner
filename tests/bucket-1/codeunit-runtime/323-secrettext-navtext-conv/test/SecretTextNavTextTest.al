/// Tests for SecretText → NavText conversion in MockHttpClient (issue #1533).
///
/// BC emits NavSecretText where MockHttpClient mock methods expect NavText.
/// Overloads accepting NavSecretText must exist in the mock.
codeunit 1320001 "STN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "STN Src";

    // ── UseWindowsAuthentication with SecretText ─────────────────────────────

    /// Positive: UseWindowsAuthentication(SecretText, SecretText) must not throw.
    [Test]
    procedure UseWindowsAuth_SecretText_NoError()
    begin
        Src.UseWindowsAuthWithSecret('user1', 'pass1');
        // [THEN] No error — NavSecretText overload exists
    end;

    /// Positive: UseWindowsAuthentication(SecretText, SecretText, SecretText) must not throw.
    [Test]
    procedure UseWindowsAuthDomain_SecretText_NoError()
    begin
        Src.UseWindowsAuthDomainWithSecret('user1', 'pass1', 'CORP');
        // [THEN] No error
    end;

    // ── AddCertificate with SecretText ────────────────────────────────────────

    /// Positive: AddCertificate(SecretText) must not throw.
    [Test]
    procedure AddCert_SecretText_NoError()
    begin
        Src.AddCertWithSecret('abc123thumbprint');
        // [THEN] No error
    end;

    /// Positive: AddCertificate(SecretText, SecretText) must not throw.
    [Test]
    procedure AddCertWithPassword_SecretText_NoError()
    begin
        Src.AddCertWithPasswordSecret('abc123thumbprint', 'certpass');
        // [THEN] No error
    end;
}
