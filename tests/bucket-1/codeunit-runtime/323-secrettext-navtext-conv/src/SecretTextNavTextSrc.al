/// Source helper for SecretText → NavText conversion tests (issue #1533).
///
/// These procedures exercise patterns where BC emits NavSecretText where
/// MockHttpClient methods expect NavText. The rewriter passes NavSecretText;
/// overloads accepting NavSecretText must exist in the mock.
codeunit 1320002 "STN Src"
{
    /// UseWindowsAuthentication called with SecretText variables.
    /// BC emits: client.ALUseWindowsAuthentication(DataError, NavSecretText, NavSecretText)
    procedure UseWindowsAuthWithSecret(plainUser: Text; plainPass: Text)
    var
        client: HttpClient;
        username: SecretText;
        password: SecretText;
    begin
        username := plainUser;
        password := plainPass;
        client.UseWindowsAuthentication(username, password);
    end;

    /// UseWindowsAuthentication 3-param called with SecretText variables.
    /// BC emits: client.ALUseWindowsAuthentication(DataError, NavSecretText, NavSecretText, NavSecretText)
    procedure UseWindowsAuthDomainWithSecret(plainUser: Text; plainPass: Text; plainDomain: Text)
    var
        client: HttpClient;
        username: SecretText;
        password: SecretText;
        domain: SecretText;
    begin
        username := plainUser;
        password := plainPass;
        domain := plainDomain;
        client.UseWindowsAuthentication(username, password, domain);
    end;

    /// AddCertificate(SecretText) — thumbprint as SecretText.
    /// BC emits: client.ALAddCertificate(DataError, NavSecretText)
    procedure AddCertWithSecret(plainThumbprint: Text)
    var
        client: HttpClient;
        thumbprint: SecretText;
    begin
        thumbprint := plainThumbprint;
        client.AddCertificate(thumbprint);
    end;

    /// AddCertificate(SecretText, SecretText) — thumbprint + password as SecretText.
    /// BC emits: client.ALAddCertificate(DataError, NavSecretText, NavSecretText)
    procedure AddCertWithPasswordSecret(plainThumbprint: Text; plainCertPass: Text)
    var
        client: HttpClient;
        thumbprint: SecretText;
        certPass: SecretText;
    begin
        thumbprint := plainThumbprint;
        certPass := plainCertPass;
        client.AddCertificate(thumbprint, certPass);
    end;
}
