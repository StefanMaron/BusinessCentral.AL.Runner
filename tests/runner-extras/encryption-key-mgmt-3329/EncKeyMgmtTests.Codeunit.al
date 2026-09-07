/// #3329 — the tenant encryption KEY-MANAGEMENT surface.
///
/// ALSystemEncryption.ALCreateKey/ALDeleteKey/ALExportKey/ALImportKey each resolve a
/// TenantRsaEncryptionProvider whose first act is to read
/// NavTenant.GetEncryptionKeyFileName -> NavDatabase.TenantProperties, and that ctor refuses a
/// database which is not a SQL tenant database. The runner has none, so all four raised
/// "ArgumentException: The given database is not a tenant database" — which is how all 32
/// tests of MS's Tests-Cash Flow Codeunit135203 failed: its shared Initialize calls
/// Codeunit 1266 DisableEncryption, and that reaches ALDeleteKey.
///
/// Why here and not in the upstream corpus: see this app's app.json "description". Short
/// version, both halves measured — the corpus tier PATCHES this surface out (bc-linux
/// StartupHook Patch #26 no-ops CreateKey/DeleteKey/ImportKey/ExportKey and hardcodes
/// IsKeyCreated true), so a result there would measure the patch rather than BC; and half the
/// surface will not compile for a Cloud target anyway (AL0296, OnPrem scope).
///
/// Test isolation here is per codeunit, not per test, so each test calls Initialize() to
/// normalise the key state rather than inheriting whatever the previous one left.
codeunit 65750 "Enc Key Mgmt Tests"
{
    Subtype = Test;

    var
        CryptographyManagement: Codeunit "Cryptography Management";

    local procedure Initialize()
    begin
        if not EncryptionEnabled() then
            CreateEncryptionKey();
    end;

    /// asserterror on its own proves only that SOMETHING threw, and the #3329 defect threw
    /// too — so a test that only checks the error text is non-empty passes against the very
    /// build these tests exist to reject. Every negative test below names its message.
    local procedure AssertErrorText(Expected: Text; Context: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if Actual = '' then
            Error('%1: expected an error, got none', Context);
        if StrPos(Actual, Expected) = 0 then
            Error('%1: expected an error containing\  %2\got\  %3', Context, Expected, Actual);
    end;

    // ── The reported failure: Codeunit 1266 -> 1279 -> ALDeleteKey ────────────────
    [Test]
    procedure DisableEncryption_TurnsEncryptionOff()
    begin
        Initialize();
        if not CryptographyManagement.IsEncryptionEnabled() then
            Error('precondition: encryption must be enabled before DisableEncryption');
        if not CryptographyManagement.IsEncryptionPossible() then
            Error('precondition: the key must be present before DisableEncryption');

        CryptographyManagement.DisableEncryption(true);

        if CryptographyManagement.IsEncryptionEnabled() then
            Error('IsEncryptionEnabled must be FALSE after DisableEncryption');
        if CryptographyManagement.IsEncryptionPossible() then
            Error('IsEncryptionPossible must be FALSE after DisableEncryption');
        if EncryptionEnabled() then
            Error('EncryptionEnabled() must agree with IsEncryptionEnabled()');
        if EncryptionKeyExists() then
            Error('EncryptionKeyExists() must agree with IsEncryptionPossible()');

        // Negative: encrypting with no key must be refused, never answered with a default.
        asserterror CryptographyManagement.EncryptText('some-plaintext');
        AssertErrorText('Encryption is either not enabled or the encryption key cannot be found',
            'EncryptText with no key');
    end;

    [Test]
    procedure EnableEncryption_AfterDisable_RestoresARealKey()
    var
        Cipher: Text;
    begin
        Initialize();
        CryptographyManagement.DisableEncryption(true);
        if CryptographyManagement.IsEncryptionEnabled() then
            Error('precondition: encryption must be off');

        CryptographyManagement.EnableEncryption(true);

        if not CryptographyManagement.IsEncryptionEnabled() then
            Error('IsEncryptionEnabled must be TRUE after EnableEncryption');
        if not CryptographyManagement.IsEncryptionPossible() then
            Error('IsEncryptionPossible must be TRUE after EnableEncryption');

        Cipher := CryptographyManagement.EncryptText('round-trip-me');
        if Cipher = 'round-trip-me' then
            Error('EncryptText returned the plaintext unchanged');
        if CryptographyManagement.Decrypt(Cipher) <> 'round-trip-me' then
            Error('Decrypt did not return the plaintext, got: %1', CryptographyManagement.Decrypt(Cipher));
    end;

    [Test]
    procedure CreateKey_WhenOneAlreadyExists_IsRefused()
    begin
        Initialize();
        if not EncryptionEnabled() then
            Error('precondition: a key must be present');

        asserterror CreateEncryptionKey();
        AssertErrorText('Unable to create a new encryption key. An encryption key already exists.',
            'CreateEncryptionKey over an existing key');

        // The refused create must not have disturbed the key that was already there.
        if not EncryptionEnabled() then
            Error('the existing key must survive a refused CreateEncryptionKey');
    end;

    // ── ALDeleteKey runs BC's DecryptTenantData first, so encrypted isolated storage
    //    survives the key going away. Without it, BC's real ALIsolatedStorage.Get raises
    //    IsolatedStorageNoEncryptionKey on the row it can no longer decrypt.
    [Test]
    procedure DeleteKey_LeavesEncryptedIsolatedStorageReadable()
    var
        V: Text;
    begin
        Initialize();
        if not IsolatedStorage.SetEncrypted('enc-3329-survives', 'plaintext-survives') then
            Error('SetEncrypted must return true');

        CryptographyManagement.DisableEncryption(true);

        if not IsolatedStorage.Get('enc-3329-survives', V) then
            Error('Get must still find the entry after the key was deleted');
        if V <> 'plaintext-survives' then
            Error('entry must read back as plaintext after the key was deleted, got: %1', V);
    end;

    [Test]
    procedure ExportImportKey_RoundTripsKeyAndData()
    var
        KeyFile: Text;
        V: Text;
    begin
        Initialize();
        if not IsolatedStorage.SetEncrypted('enc-3329-roundtrip', 'plaintext-roundtrip') then
            Error('SetEncrypted must return true');

        KeyFile := ExportEncryptionKey('key-file-password');
        if KeyFile = '' then
            Error('ExportEncryptionKey must return a file name');

        DeleteEncryptionKey();
        if EncryptionEnabled() then
            Error('precondition: encryption must be off after DeleteEncryptionKey');

        ImportEncryptionKey(KeyFile, 'key-file-password');
        if not EncryptionEnabled() then
            Error('EncryptionEnabled must be TRUE after ImportEncryptionKey');
        if not EncryptionKeyExists() then
            Error('EncryptionKeyExists must be TRUE after ImportEncryptionKey');

        if not IsolatedStorage.Get('enc-3329-roundtrip', V) then
            Error('Get must find the entry after the key round trip');
        if V <> 'plaintext-roundtrip' then
            Error('entry must survive export/delete/import unchanged, got: %1', V);
    end;

    [Test]
    procedure ImportKey_WithWrongPassword_IsRefused()
    var
        KeyFile: Text;
    begin
        Initialize();
        KeyFile := ExportEncryptionKey('right-password');
        DeleteEncryptionKey();

        asserterror ImportEncryptionKey(KeyFile, 'wrong-password');
        AssertErrorText(
            'The import failed. The provided encryption key file contains invalid data and could not be imported.',
            'ImportEncryptionKey with the wrong password');

        // A refused import must not install a key.
        if EncryptionEnabled() then
            Error('a refused import must leave encryption disabled');
    end;

    [Test]
    procedure ImportKey_MissingFile_IsRefused()
    begin
        Initialize();
        DeleteEncryptionKey();

        asserterror ImportEncryptionKey('no-such-file-3329.key', 'pw');
        AssertErrorText('File no-such-file-3329.key was not found.',
            'ImportEncryptionKey on a missing file');
        if EncryptionEnabled() then
            Error('a refused import must leave encryption disabled');
    end;

    [Test]
    procedure ImportKey_DifferentKeyWhileOneExists_IsRefused()
    var
        KeyFile: Text;
        Cipher: Text;
    begin
        Initialize();
        KeyFile := ExportEncryptionKey('pw');   // key A

        DeleteEncryptionKey();
        CreateEncryptionKey();                  // key B, a different key
        Cipher := CryptographyManagement.EncryptText('under-key-B');

        asserterror ImportEncryptionKey(KeyFile, 'pw');
        AssertErrorText('A different encryption key is already registered in the database.',
            'ImportEncryptionKey of a different key over an existing one');

        // Key B must be untouched — data encrypted under it still decrypts.
        if CryptographyManagement.Decrypt(Cipher) <> 'under-key-B' then
            Error('the refused import must leave key B in place');
    end;

    [Test]
    procedure ExportKey_WithNoKey_IsRefused()
    var
        KeyFile: Text;
    begin
        Initialize();
        DeleteEncryptionKey();

        asserterror KeyFile := ExportEncryptionKey('pw');
        AssertErrorText('An encryption key is required to complete the request.',
            'ExportEncryptionKey with no key');
    end;

    // A new key must not be able to read the old key's ciphertext. Without the key tag in
    // the runner's envelope this is decided by AES-CBC padding alone, which validates by
    // chance about once in 256 and would then hand AL silent garbage.
    [Test]
    procedure NewKey_CannotDecryptOldCiphertext()
    var
        Cipher: Text;
    begin
        Initialize();
        Cipher := CryptographyManagement.EncryptText('sealed-under-key-A');

        DeleteEncryptionKey();
        CreateEncryptionKey();

        asserterror CryptographyManagement.Decrypt(Cipher);
        // Named message, not a bare asserterror: this is where a wrong-key decrypt could
        // otherwise slip through on AES padding luck, or on the #3329 ArgumentException.
        AssertErrorText(
            'ALSystemEncryption.ALDecrypt: ciphertext was encrypted with a different key than the one now in effect',
            'Decrypt of key A ciphertext under key B');

        // ...and key B is a working key, so the refusal above is about the key, not about
        // encryption being unavailable.
        if CryptographyManagement.Decrypt(CryptographyManagement.EncryptText('sealed-under-key-B'))
            <> 'sealed-under-key-B' then
            Error('key B must round-trip its own ciphertext');
    end;
}
