/// <summary>
/// IsolatedStorage Set/Contains/Get/Delete round-trip — the storage pattern
/// SPBLIC's Extension Setup uses inside Pageworks's install trigger
/// (SetAppValue → Repository Contains/Set). Values must round-trip exactly and
/// deletes must be observable; a missing key must report false, never throw.
/// </summary>
codeunit 61250 "Isolated Storage Tests"
{
    Subtype = Test;

    [Test]
    procedure SetContainsGet_RoundTripsExactValue()
    var
        Value: Text;
    begin
        if not IsolatedStorage.Set('its-key', 'its-value') then
            Error('IsolatedStorage.Set must return true.');
        if not IsolatedStorage.Contains('its-key') then
            Error('IsolatedStorage.Contains must see the stored key.');
        if not IsolatedStorage.Get('its-key', Value) then
            Error('IsolatedStorage.Get must return true for a stored key.');
        if Value <> 'its-value' then
            Error('IsolatedStorage.Get must round-trip the exact value, got %1.', Value);
    end;

    [Test]
    procedure Delete_RemovesTheEntry()
    begin
        IsolatedStorage.Set('its-doomed', 'x');
        if not IsolatedStorage.Delete('its-doomed') then
            Error('IsolatedStorage.Delete must return true for an existing key.');
        if IsolatedStorage.Contains('its-doomed') then
            Error('Deleted key must not be contained.');
    end;

    [Test]
    procedure EncryptDecrypt_RoundTripsAndIsNotPlaintext()
    var
        CipherText: Text;
    begin
        if not EncryptionEnabled() then
            Error('EncryptionEnabled must be true on the runner (in-process AES envelope).');
        CipherText := Encrypt('its-secret');
        if CipherText = 'its-secret' then
            Error('Encrypt must not return the plaintext.');
        if Decrypt(CipherText) <> 'its-secret' then
            Error('Decrypt must round-trip the exact plaintext.');
    end;

    [Test]
    procedure SetEncrypted_GetRoundTripsPlaintext()
    var
        Value: Text;
    begin
        if not IsolatedStorage.SetEncrypted('its-enc-key', 'its-enc-value') then
            Error('IsolatedStorage.SetEncrypted must return true.');
        if not IsolatedStorage.Get('its-enc-key', Value) then
            Error('IsolatedStorage.Get must find the encrypted entry.');
        if Value <> 'its-enc-value' then
            Error('Encrypted entry must round-trip to plaintext on Get, got %1.', Value);
    end;

    [Test]
    procedure Get_MissingKey_ReturnsFalse()
    var
        Value: Text;
    begin
        if IsolatedStorage.Get('its-absent', Value) then
            Error('IsolatedStorage.Get must return false for a missing key.');
        if IsolatedStorage.Contains('its-absent') then
            Error('IsolatedStorage.Contains must be false for a missing key.');
    end;
}
