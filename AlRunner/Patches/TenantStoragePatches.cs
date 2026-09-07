// TenantStoragePatches — in-scope faithful replacement for the LOWEST layer of
// ALIsolatedStorage / ALSystemEncryption (in-memory store, real in-process AES envelope).
//
// History (#1883): this file used to ALSO JmpHook the higher AL-facing ALIsolatedStorage.AL*
// static methods (ALSet/ALGet/ALContains/ALDelete/ALSetEncrypted, 17 registrations). JmpHook
// is disabled by default (Cecil-only), so those 17 were silently orphaned — BC's real,
// unpatched ALIsolatedStorage.AL* bodies already run instead, and they delegate entirely to
// IsolatedStorageRepository.Set/Get/Contains/Delete and ALSystemEncryption.ALEncrypt/ALDecrypt/
// ALKeyExists/ALEncryptionEnabled (decompiled and confirmed — GetCompanyByScope/GetUserByScope
// read NavCurrentThread.Session.Company/User, both seeded by BcRuntime's Cecil-owned NavSession
// getter cluster, so no NRE). Both of those lower-level targets are Cecil-rewritten onto the
// Repo_*/SysEnc_* helpers below (see NclCecilRewrite.cs, "IsolatedStorageRepository" /
// "ALSystemEncryption" blocks) — an ALWAYS-ON mechanism, independent of JmpHook. So the higher
// 17 JmpHook registrations were pure duplication of a job the lower Cecil rewrite already did
// correctly; they were deleted outright, along with their now-dead replacement bodies
// (ALSet_2/_3/_Secret_3, ALSetEncrypted_2/_Secret_2/_3/_Secret_3, ALGet_Text_2/_3/_Secret_2/_3,
// ALContains_2/_3, ALDelete_1/_2, ALIsoSet_6, ALIsoGet_5_Text, SetImpl/GetTextImpl/GetSecretImpl).
// Verified empirically across every DataScope value, the SecretText overloads, the Contains
// IsSecret flag, and SetEncrypted(SecretText) — see
// tests/runner-extras/standalone-suites/isolated-storage-1883/.
//
// What remains here — the ALWAYS-ON, Cecil-consumed faithful implementation:
//   - Repo_Set / Repo_Get / Repo_Contains_6 / Repo_Contains_5 / Repo_Delete: replace
//     IsolatedStorageRepository's five statics, whose real bodies NRE on the skeleton
//     (open tenant-scoped NavRecord 2000000107 via state the skeleton lacks).
//   - SysEnc_ALEncrypt / SysEnc_ALDecrypt / SysEnc_ALKeyExists / SysEnc_ALEncryptionEnabled:
//     replace ALSystemEncryption's four statics, whose real bodies resolve a tenant RSA/
//     KeyVault provider that NREs on the skeleton ("not a tenant database").
//
// Faithfulness (loud-failures.md):
//   - SetEncrypted / GetEncrypted use real AES-256-CBC with a random 16-byte IV prepended
//     to the ciphertext. The key is derived deterministically (PBKDF2-SHA256 over a fixed
//     skeleton-runner salt) so a SetEncrypted in one test step round-trips through Get
//     in the next step, but encrypted-bytes ≠ plaintext (negative tests like
//     "DifferentKeysDifferentValues" pass because we store one row per key and AES output
//     diverges with each random IV).
//   - The store row is verbatim ciphertext; the REAL (unpatched) ALIsolatedStorage.Get body
//     decrypts it before returning to AL — see the Repo_Set/Repo_Get comments below.
//   - DataScope is honoured: Company-scoped entries include a scope-dependent qualifier in
//     the composite dictionary key so the BC contract (different scope → different store)
//     holds — see ComposeKey.
//   - Set/Get/Contains/Delete return true on success (matches BC semantics — see
//     test bucket 314-void-returning-bool which asserts `if not Set(...)` branch is skipped).

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Microsoft.Dynamics.Nav.Types.Exceptions.Encryption;

namespace AlRunner.Patches;

public static class TenantStoragePatches
{
    // Values are BC's own Microsoft.Dynamics.Nav.Types.EncryptionStatus ordinals
    // (PlainText=0, Encrypted=1, PendingForEncryption=2) — Repo_Get returns the int
    // straight through, and BC's real ALIsolatedStorage.Get branches on it.
    private enum Encryption { None, Encrypted, PendingForEncryption }

    private sealed record Entry(string Ciphertext, Encryption Status, bool IsSecret);

    // Composite key: scope+companyQualifier+userQualifier+key. Companies / users are
    // scope-dependent — Module ignores both, Company keys on company, User on user.
    private static readonly ConcurrentDictionary<string, Entry> _store = new();

    public static void ResetForTest()
    {
        _store.Clear();
        // The key ledger is per-test for the same reason the store is: BC rolls the
        // tenant-properties row back with the rest of the test transaction.
        _encKey = DefaultKeyState();
    }

    internal static object CaptureInstallBaseline() => _store.ToArray();

    internal static void RestoreInstallBaseline(object? snapshot)
    {
        _store.Clear();
        if (snapshot is not KeyValuePair<string, Entry>[] entries) return;
        foreach (var entry in entries)
            _store[entry.Key] = entry.Value;
    }

    /// <summary>Write the isolated-storage half of an install baseline to the on-disk
    /// baseline cache (see RecordPatches.InstallBaselineDisk). Lives here, not in the codec,
    /// because <see cref="Entry"/> is private to this store — the codec should not have to
    /// know its shape, and this way a field added to Entry cannot be silently dropped from
    /// the persisted form.</summary>
    internal static void SerializeInstallBaseline(BinaryWriter w, object? snapshot)
    {
        var entries = snapshot as KeyValuePair<string, Entry>[] ?? Array.Empty<KeyValuePair<string, Entry>>();
        w.Write(entries.Length);
        foreach (var (key, entry) in entries)
        {
            w.Write(key);
            w.Write(entry.Ciphertext);
            w.Write((int)entry.Status);
            w.Write(entry.IsSecret);
        }
    }

    /// <summary>Sorted, fully-expanded text form of the isolated-storage half of an install
    /// baseline — the input to the round-trip digest the on-disk cache logs, so a restored
    /// snapshot can be compared against the captured one field by field rather than by count.
    /// Sorted because the underlying store is a ConcurrentDictionary and its enumeration order
    /// is not meaningful.</summary>
    internal static IEnumerable<string> DescribeInstallBaseline(object? snapshot)
    {
        var entries = snapshot as KeyValuePair<string, Entry>[] ?? Array.Empty<KeyValuePair<string, Entry>>();
        return entries
            .Select(e => $"iso|{e.Key}|{e.Value.Ciphertext}|{(int)e.Value.Status}|{e.Value.IsSecret}")
            .OrderBy(x => x, StringComparer.Ordinal);
    }

    /// <summary>Counterpart of <see cref="SerializeInstallBaseline"/>. Returns a value shaped
    /// exactly like <see cref="CaptureInstallBaseline"/>'s, so
    /// <see cref="RestoreInstallBaseline"/> cannot tell the two apart.</summary>
    internal static object DeserializeInstallBaseline(BinaryReader r)
    {
        var count = r.ReadInt32();
        var entries = new KeyValuePair<string, Entry>[count];
        for (var i = 0; i < count; i++)
        {
            var key = r.ReadString();
            var ciphertext = r.ReadString();
            var status = (Encryption)r.ReadInt32();
            var isSecret = r.ReadBoolean();
            entries[i] = new KeyValuePair<string, Entry>(key, new Entry(ciphertext, status, isSecret));
        }
        return entries;
    }

    // TEMPORARY (memory-census diagnostic) — total stored entries. See MemoryCensus.cs.
    internal static int CensusEntryCount() => _store.Count;

    // ── Key composition ────────────────────────────────────────────────────────
    private static string ComposeKey(DataScope scope, string key)
    {
        // DataScope: Module=0 (no qualifier), Company=1 (company), User=2 (user),
        // CompanyAndUser=3 (both). Skeleton runner has a fixed default company
        // ("CRONUS") and user (anonymous SID); but for testing purposes any
        // consistent qualifier suffices — the BC contract is "different scope →
        // different store", and that holds as long as the suffix is scope-dependent.
        string suffix = scope switch
        {
            DataScope.Module         => string.Empty,
            DataScope.Company        => "|co=CRONUS",
            DataScope.User           => "|u=__skel__",
            DataScope.CompanyAndUser => "|co=CRONUS|u=__skel__",
            _                        => "|s=" + (int)scope,
        };
        return $"s={(int)scope}|k={key}{suffix}";
    }

    // (Crypto envelopes live in the ALSystemEncryption section below — Encrypt/Decrypt
    // are routed through SysEnc_ALEncrypt / SysEnc_ALDecrypt so AL's
    // SetEncrypted → ALEncrypt → Set / Get → ALDecrypt symmetry is preserved.)

    // ── IsolatedStorageRepository.* (lowest level — AL output hits these for
    //    Contains/Delete and for Set/Get via ALIsolatedStorage delegation) ──────
    // NOTE (Cecil migration): with the AL-facing ALIsolatedStorage bodies running
    // REAL code, encryption happens ABOVE this layer — ALSetEncrypted calls
    // ALSystemEncryption.ALEncrypt before Repository.Set, and the real Get calls
    // ALDecrypt when the stored status is Encrypted. The repository must therefore
    // store and return the value VERBATIM (exactly like BC's table 2000000107 row),
    // only remembering the EncryptionStatus — re-encrypting here would double-wrap.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Repo_Set(DataError de, NavGuid appId, DataScope scope,
                                string companyName, NavGuid userId, string key, string value,
                                int encryptionStatus, /* TargetValueType */ int targetValueType)
    {
        var mode = (Encryption)encryptionStatus;
        var isSecret = targetValueType == 1;
        _store[ComposeKey(scope, key)] = new Entry(value, mode, isSecret);
        return true;
    }

    // BC return type is ValueTuple<bool, ...>. Probe showed return ValueTuple`2.
    // We need to construct that or — simpler — handle this by hooking the higher
    // ALIsolatedStorage.Get instead, but AL output sometimes lands directly here.
    // Strategy: return tuple (found, _) where _ is the original encryption status.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (bool, int) Repo_Get(DataError de, NavGuid appId, DataScope scope,
                                                    int targetValueType, string companyName, NavGuid userId,
                                                    string key, ByRef<NavText> value)
    {
        if (!_store.TryGetValue(ComposeKey(scope, key), out var entry))
        {
            value.Value = new NavText(string.Empty);
            return (false, 0 /* EncryptionStatus.PlainText */);
        }
        // Verbatim, like BC's stored row — the REAL ALIsolatedStorage.Get body
        // ALDecrypts when the returned status is Encrypted (see Repo_Set note).
        value.Value = new NavText(entry.Ciphertext);
        return (true, (int)entry.Status);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Repo_Contains_6(NavGuid appId, DataScope scope, string companyName,
                                       NavGuid userId, string key, ref bool isSecret)
    {
        if (_store.TryGetValue(ComposeKey(scope, key), out var entry))
        {
            isSecret = entry.IsSecret;
            return true;
        }
        isSecret = false;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Repo_Contains_5(NavGuid appId, DataScope scope, string companyName,
                                       NavGuid userId, string key)
        => _store.ContainsKey(ComposeKey(scope, key));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Repo_Delete(DataError de, NavGuid appId, DataScope scope,
                                   string companyName, NavGuid userId, string key)
    {
        // BC's Delete returns true when the key existed (was actually deleted).
        return _store.TryRemove(ComposeKey(scope, key), out _);
    }

    // ── ALSystemEncryption.* — the tenant encryption KEY LEDGER, and the in-process
    //    AES envelope that Encrypt/Decrypt ride on. ────────────────────────────────
    //
    // BC's real chain is ALSystemEncryption → TenantRsaEncryptionProvider →
    // NavTenant.Get/SetEncryptionKeyFileName → NavDatabase.TenantProperties, whose ctor
    // refuses a database that is not a SQL tenant database. The runner has none, so every
    // entry point that touched key state raised a bare ArgumentException (#3329). This
    // ledger replaces that persistence layer; nothing else about BC's surface changes.
    //
    // Observably equivalent (loud-failures.md): AL can never see BC's RSA key material or
    // its key file. What it CAN see is KEYEXISTS / ENCRYPTIONENABLED, whether ENCRYPT and
    // DECRYPT round-trip, and which BC exception each refusal raises — and each of those
    // is answered from real state here rather than a default. Creating a key while one
    // exists raises NavEncryptionCreatedException; encrypting or decrypting with no key
    // raises NavEncryptionNotCreatedException; deleting a key first decrypts isolated
    // storage exactly as BC's DecryptTenantData does, so a SetEncrypted value stays
    // readable afterwards; and ciphertext written under one key does not decrypt under the
    // next. The one thing the runner does NOT model is a fresh tenant with no key: it
    // starts with a key present, which is what the two hardcoded `true` predicates this
    // ledger replaces already asserted.
    private sealed record EncryptionKeyState(byte[] Material, string Hash);

    private const string EnvelopePrefix = "RNR1";
    private const string KeyFileMagic = "AL-RUNNER-ENCRYPTION-KEY-V1:";

    private static readonly byte[] _defaultSysEncKey = DeriveSysKey();
    private static EncryptionKeyState? _encKey = DefaultKeyState();

    private static EncryptionKeyState DefaultKeyState()
        => new(_defaultSysEncKey, KeyHash(_defaultSysEncKey));

    // Deterministic across processes on purpose: an isolated-storage row encrypted while
    // an install baseline was captured has to decrypt in the process that restores it.
    private static byte[] DeriveSysKey()
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            "al-runner-v2-system-encryption",
            Encoding.UTF8.GetBytes("al-runner-skeleton-salt-2026"),
            10_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static string KeyHash(byte[] material)
        => Convert.ToHexString(SHA256.HashData(material));

    // ── Envelope: "RNR1:<16-hex key tag>:<base64(iv‖ciphertext)>" ───────────────────
    // The key tag makes "wrong key" a deterministic refusal. Without it the only signal
    // is AES-CBC padding, which validates by chance about once in 256 and would then hand
    // AL silent garbage — the failure mode loud-failures.md exists to prevent.
    private static string EncryptWith(byte[] key, string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var pt = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
        var ct = enc.TransformFinalBlock(pt, 0, pt.Length);
        var blob = new byte[16 + ct.Length];
        Buffer.BlockCopy(aes.IV, 0, blob, 0, 16);
        Buffer.BlockCopy(ct,     0, blob, 16, ct.Length);
        return $"{EnvelopePrefix}:{KeyHash(key)[..16]}:{Convert.ToBase64String(blob)}";
    }

    /// <summary>Inverse of <see cref="EncryptWith"/>. Throws <see cref="CryptographicException"/>
    /// — never a default — when the envelope is foreign or was sealed under another key;
    /// callers map that onto the BC exception their surface raises.</summary>
    private static string DecryptWith(byte[] key, string envelope)
    {
        var parts = (envelope ?? string.Empty).Split(':');
        if (parts.Length != 3 || parts[0] != EnvelopePrefix)
            throw new CryptographicException(
                "ciphertext was not produced by this runner's ALEncrypt");
        if (!string.Equals(parts[1], KeyHash(key)[..16], StringComparison.Ordinal))
            throw new CryptographicException(
                "ciphertext was encrypted with a different key than the one now in effect");
        var raw = Convert.FromBase64String(parts[2]);
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = new byte[16];
        Buffer.BlockCopy(raw, 0, iv, 0, 16);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(dec.TransformFinalBlock(raw, 16, raw.Length - 16));
    }

    // ── AL-facing statics (Cecil-rewritten onto these — see NclCecilRewrite.Records.cs) ──

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string SysEnc_ALEncrypt(string plaintext)
    {
        var k = _encKey ?? throw new NavEncryptionNotCreatedException();
        return EncryptWith(k.Material, plaintext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string SysEnc_ALDecrypt(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        var k = _encKey ?? throw new NavEncryptionNotCreatedException();
        try
        {
            return DecryptWith(k.Material, ciphertext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Same mapping RsaEncryptionProviderBase.Decrypt applies to a bad payload.
            throw new NavEncryptionException("ALDecrypt: " + ex.Message, ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SysEnc_ALKeyExists() => _encKey != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SysEnc_ALEncryptionEnabled() => _encKey != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SysEnc_ALCreateKey(DataError errorLevel)
    {
        try
        {
            // RsaEncryptionProviderBase.CreateKey: refuses when a key is already created.
            if (_encKey != null) throw new NavEncryptionCreatedException();
            InstallKey(RandomNumberGenerator.GetBytes(32));
            return true;
        }
        catch (NavBaseException) when (errorLevel == DataError.TrapError)
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SysEnc_ALDeleteKey()
    {
        var k = _encKey;
        if (k != null)
        {
            // ALDeleteKey calls DecryptTenantData() BEFORE the provider drops the key, so
            // every Encrypted row is decrypted in place and parked at PendingForEncryption
            // and survives the key going away (IsolatedStorageRepository.ChangeEncrptionStatus).
            foreach (var kv in _store.ToArray())
            {
                if (kv.Value.Status != Encryption.Encrypted) continue;
                _store[kv.Key] = kv.Value with
                {
                    Ciphertext = DecryptWith(k.Material, kv.Value.Ciphertext),
                    Status = Encryption.PendingForEncryption,
                };
            }
        }
        // BC's DeleteKey is idempotent — deleting with no key present is not an error.
        _encKey = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string SysEnc_ALExportKey(string password)
    {
        // RsaEncryptionProviderBase.ExportKey → RunCryptoProviderMethod →
        // RequireKeyCreatedAndPresent, which refuses with this exception when no key exists.
        var k = _encKey ?? throw new NavEncryptionNotCreatedException();
        var payload = KeyFileMagic + Convert.ToBase64String(k.Material);
        if (!string.IsNullOrEmpty(password))
            payload = EncryptWith(PasswordKey(password), payload);
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-navserver", "encryption-keys");
        Directory.CreateDirectory(dir);
        // BC returns a server-side temp path the AL caller reads and then File.Erase()s.
        var file = Path.Combine(dir, Guid.NewGuid().ToString() + ".key");
        File.WriteAllText(file, payload, Encoding.UTF8);
        return file;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SysEnc_ALImportKey(DataError errorLevel, string keyFileName, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyFileName) || !File.Exists(keyFileName))
                throw new NavNCLFileNotFoundException(keyFileName ?? string.Empty);
            var text = File.ReadAllText(keyFileName, Encoding.UTF8);
            if (!string.IsNullOrEmpty(password))
            {
                try { text = DecryptWith(PasswordKey(password), text); }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    throw new NavEncryptionInvalidKeyFileException(ex);
                }
            }
            if (!text.StartsWith(KeyFileMagic, StringComparison.Ordinal))
                throw new NavEncryptionInvalidKeyFileException(
                    "ImportKey: the file is not an AL Runner encryption key file, "
                    + "or the wrong password was supplied.");
            byte[] material;
            try { material = Convert.FromBase64String(text[KeyFileMagic.Length..]); }
            catch (FormatException ex) { throw new NavEncryptionInvalidKeyFileException(ex); }

            // RsaEncryptionProviderBase.ImportKey compares the imported key's hash against
            // the stored one and refuses a DIFFERENT key; re-importing the same key is fine.
            var hash = KeyHash(material);
            if (_encKey != null && !string.Equals(_encKey.Hash, hash, StringComparison.Ordinal))
                throw new NavEncryptionExistingKeyImportException();
            InstallKey(material);
            return true;
        }
        catch (NavBaseException) when (errorLevel == DataError.TrapError)
        {
            return false;
        }
    }

    private static byte[] PasswordKey(string password)
    {
        using var kdf = new Rfc2898DeriveBytes(
            password, Encoding.UTF8.GetBytes("al-runner-key-file-salt-2026"),
            100_000, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }

    /// <summary>Install <paramref name="material"/> as the tenant key and run BC's
    /// EncryptPendingData: every row parked at PendingForEncryption by a preceding
    /// DeleteKey is re-encrypted under the new key, which is what makes an
    /// export → delete → import round trip leave isolated storage where it started.</summary>
    private static void InstallKey(byte[] material)
    {
        _encKey = new EncryptionKeyState(material, KeyHash(material));
        foreach (var kv in _store.ToArray())
        {
            if (kv.Value.Status != Encryption.PendingForEncryption) continue;
            _store[kv.Key] = kv.Value with
            {
                Ciphertext = EncryptWith(material, kv.Value.Ciphertext),
                Status = Encryption.Encrypted,
            };
        }
    }
}
