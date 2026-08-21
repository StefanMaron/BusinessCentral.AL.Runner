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

namespace AlRunner.Patches;

public static class TenantStoragePatches
{
    private enum Encryption { None, Encrypted }

    private sealed record Entry(string Ciphertext, Encryption Status, bool IsSecret);

    // Composite key: scope+companyQualifier+userQualifier+key. Companies / users are
    // scope-dependent — Module ignores both, Company keys on company, User on user.
    private static readonly ConcurrentDictionary<string, Entry> _store = new();

    public static void ResetForTest() => _store.Clear();

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
        var mode = encryptionStatus == 1 /* EncryptionStatus.Encrypted */ ? Encryption.Encrypted : Encryption.None;
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
        return (true, entry.Status == Encryption.Encrypted ? 1 : 0);
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

    // ── ALSystemEncryption.* (in-process AES envelope for the AL Encrypt/Decrypt
    //    surface — replaces the BC tenant-key-vault provider that's null on
    //    the skeleton runtime). Same key as IsolatedStorage's deterministic key,
    //    different prefix tag so collisions across surfaces are impossible. ─────
    private static readonly byte[] _sysEncKey = DeriveSysKey();
    private static byte[] DeriveSysKey()
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            "al-runner-v2-system-encryption",
            Encoding.UTF8.GetBytes("al-runner-skeleton-salt-2026"),
            10_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string SysEnc_ALEncrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _sysEncKey;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var pt = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
        var ct = enc.TransformFinalBlock(pt, 0, pt.Length);
        var blob = new byte[16 + ct.Length];
        Buffer.BlockCopy(aes.IV, 0, blob, 0, 16);
        Buffer.BlockCopy(ct,     0, blob, 16, ct.Length);
        // Prefix with "RNR1:" to identify our envelope cleanly.
        return "RNR1:" + Convert.ToBase64String(blob);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string SysEnc_ALDecrypt(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        if (!ciphertext.StartsWith("RNR1:"))
            // Caller passed something that wasn't produced by our ALEncrypt —
            // in real BC this would throw "not encrypted by this tenant". We
            // throw too so the test catches the misuse rather than silently
            // returning a default.
            throw new InvalidOperationException("ALDecrypt: ciphertext was not produced by this runner's ALEncrypt.");
        var raw = Convert.FromBase64String(ciphertext.Substring(5));
        using var aes = Aes.Create();
        aes.Key = _sysEncKey;
        var iv = new byte[16];
        Buffer.BlockCopy(raw, 0, iv, 0, 16);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        var pt = dec.TransformFinalBlock(raw, 16, raw.Length - 16);
        return Encoding.UTF8.GetString(pt);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SysEnc_ALKeyExists() => true;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SysEnc_ALEncryptionEnabled() => true;
}
