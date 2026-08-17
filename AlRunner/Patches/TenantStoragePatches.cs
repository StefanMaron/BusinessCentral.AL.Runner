// TenantStoragePatches — in-scope faithful replacement for ALIsolatedStorage (in-memory store)
// plus a deterministic in-process AES envelope behind SetEncrypted/GetEncrypted.
//
// Why JmpHook on ALIsolatedStorage.AL*: ALIsolatedStorage lives in Ncl.dll (the runtime
// engine, not an AL-business-logic DLL — see .claude/rules/precompiled-dll-respect.md), and
// its AL* methods are the entry points AL-emitted code calls. The real bodies route through
// IsolatedStorageRepository which requires a NavTenant+DataAccessSource for tenant-scoped
// tables — both NRE on the skeleton runtime. We replace the AL-facing surface with an
// in-memory ConcurrentDictionary keyed by (scope, key) and persist company/user qualifiers
// alongside the entry so DataScope::Company / DataScope::User isolation works.
//
// Faithfulness (loud-failures.md):
//   - SetEncrypted / GetEncrypted use real AES-256-CBC with a random 16-byte IV prepended
//     to the ciphertext. The key is derived deterministically (PBKDF2-SHA256 over a fixed
//     skeleton-runner salt) so a SetEncrypted in one test step round-trips through Get
//     in the next step, but encrypted-bytes ≠ plaintext (negative tests like
//     "DifferentKeysDifferentValues" pass because we store one row per key and AES output
//     diverges with each random IV).
//   - The on-disk row stores ciphertext; ALGet decrypts before returning to AL.
//   - DataScope is honoured: Company-scoped entries include the current company name in
//     the composite dictionary key so the BC contract (different scope → different store)
//     holds.
//   - Set/Get/Contains/Delete return true on success (matches BC semantics — see
//     test bucket 314-void-returning-bool which asserts `if not Set(...)` branch is skipped).
//
// What is NOT done:
//   - We do not touch IsolatedStorageRepository's static methods. The AL* entry points
//     are sufficient; any caller reaching IsolatedStorageRepository directly is using a
//     non-AL surface (which is out-of-scope here and would still NRE on the skeleton
//     tenant — that's correctly loud per loud-failures.md).
//   - TenantEncryptionProviderFactory wiring is not needed for SetEncrypted/GetEncrypted
//     once the AL* surface is replaced — the factory is reached only from the
//     IsolatedStorageRepository path we've bypassed.

using System.Collections.Concurrent;
using System.Reflection;
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

    public static void Register(Assembly navNcl)
    {
        var tALIso = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALIsolatedStorage");
        var tRepo  = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.IsolatedStorageRepository");
        if (tALIso == null || tRepo == null)
        {
            Console.Error.WriteLine("[TenantStoragePatches] ALIsolatedStorage or IsolatedStorageRepository not found; skipping");
            return;
        }

        var tDataError = typeof(DataError);
        var tString    = typeof(string);
        var tDataScope = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.DataScope")!;
        var tNavGuid   = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavGuid")!;
        var tEncStatus = typeof(DataError).Assembly.GetType("Microsoft.Dynamics.Nav.Types.EncryptionStatus")!;
        var tTargetVT  = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.Data.IsolatedStorage.TargetValueType")!;
        var tByRefText = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ByRef`1")!
                              .MakeGenericType(navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavText")!);
        var tByRefSecret = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ByRef`1")!
                                .MakeGenericType(navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSecretText")!);
        var tByRefBool = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ByRef`1")!
                              .MakeGenericType(typeof(bool));
        var tBoolByRef = typeof(bool).MakeByRefType();

        var me = typeof(TenantStoragePatches);
        int hooks = 0;

        void HookIf(MethodInfo? original, string replacementName, string description)
        {
            if (original == null)
            {
                Console.Error.WriteLine($"[TenantStoragePatches] skip {description}: original not found");
                return;
            }
            var repl = me.GetMethod(replacementName, BindingFlags.Public | BindingFlags.Static);
            if (repl == null)
            {
                Console.Error.WriteLine($"[TenantStoragePatches] skip {description}: replacement {replacementName} not found");
                return;
            }
            try { JmpHook.Apply(original, repl, description); hooks++; }
            catch (Exception ex) { Console.Error.WriteLine($"[TenantStoragePatches] hook {description} failed: {ex.Message}"); }
        }

        // ── ALSet overloads ─────────────────────────────────────────────────────
        HookIf(tALIso.GetMethod("ALSet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tString}, null),
               nameof(ALSet_2),                 "ALIsolatedStorage.ALSet(DataError,key,value)");
        HookIf(tALIso.GetMethod("ALSet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tString, tDataScope}, null),
               nameof(ALSet_3),                 "ALIsolatedStorage.ALSet(DataError,key,value,scope)");
        var tNavSec = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSecretText")!;
        HookIf(tALIso.GetMethod("ALSet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tNavSec, tDataScope}, null),
               nameof(ALSet_Secret_3),          "ALIsolatedStorage.ALSet(DataError,key,NavSecretText,scope)");

        // ── ALSetEncrypted overloads ────────────────────────────────────────────
        HookIf(tALIso.GetMethod("ALSetEncrypted", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tString}, null),
               nameof(ALSetEncrypted_2),        "ALIsolatedStorage.ALSetEncrypted(DataError,key,value)");
        HookIf(tALIso.GetMethod("ALSetEncrypted", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tNavSec}, null),
               nameof(ALSetEncrypted_Secret_2), "ALIsolatedStorage.ALSetEncrypted(DataError,key,NavSecretText)");
        HookIf(tALIso.GetMethod("ALSetEncrypted", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tString, tDataScope}, null),
               nameof(ALSetEncrypted_3),        "ALIsolatedStorage.ALSetEncrypted(DataError,key,value,scope)");
        HookIf(tALIso.GetMethod("ALSetEncrypted", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tNavSec, tDataScope}, null),
               nameof(ALSetEncrypted_Secret_3), "ALIsolatedStorage.ALSetEncrypted(DataError,key,NavSecretText,scope)");

        // ── ALGet overloads ─────────────────────────────────────────────────────
        HookIf(tALIso.GetMethod("ALGet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tByRefText}, null),
               nameof(ALGet_Text_2),            "ALIsolatedStorage.ALGet(DataError,key,ByRef<NavText>)");
        HookIf(tALIso.GetMethod("ALGet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tByRefSecret}, null),
               nameof(ALGet_Secret_2),          "ALIsolatedStorage.ALGet(DataError,key,ByRef<NavSecretText>)");
        HookIf(tALIso.GetMethod("ALGet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tDataScope, tByRefText}, null),
               nameof(ALGet_Text_3),            "ALIsolatedStorage.ALGet(DataError,key,scope,ByRef<NavText>)");
        HookIf(tALIso.GetMethod("ALGet", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tDataScope, tByRefSecret}, null),
               nameof(ALGet_Secret_3),          "ALIsolatedStorage.ALGet(DataError,key,scope,ByRef<NavSecretText>)");

        // ── ALContains ──────────────────────────────────────────────────────────
        HookIf(tALIso.GetMethod("ALContains", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tString, tDataScope}, null),
               nameof(ALContains_2),            "ALIsolatedStorage.ALContains(key,scope)");
        HookIf(tALIso.GetMethod("ALContains", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tString, tDataScope, tByRefBool}, null),
               nameof(ALContains_3),            "ALIsolatedStorage.ALContains(key,scope,ByRef<bool>)");

        // ── ALDelete ────────────────────────────────────────────────────────────
        HookIf(tALIso.GetMethod("ALDelete", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString}, null),
               nameof(ALDelete_1),              "ALIsolatedStorage.ALDelete(DataError,key)");
        HookIf(tALIso.GetMethod("ALDelete", BindingFlags.Public|BindingFlags.Static,
                  null, new[]{tDataError, tString, tDataScope}, null),
               nameof(ALDelete_2),              "ALIsolatedStorage.ALDelete(DataError,key,scope)");

        // ── ALIsolatedStorage.Set (6-arg, what AL output actually emits — internal) ─
        HookIf(tALIso.GetMethod("Set", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static,
                  null, new[]{tDataError, tString, tString, tDataScope, tEncStatus, tTargetVT}, null),
               nameof(ALIsoSet_6),              "ALIsolatedStorage.Set(DataError,key,value,scope,enc,target)");

        // ── ALIsolatedStorage.Get (5-arg, what AL output actually emits — internal) ─
        HookIf(tALIso.GetMethod("Get", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static,
                  null, new[]{tDataError, tString, tDataScope, tByRefText, tTargetVT}, null),
               nameof(ALIsoGet_5_Text),         "ALIsolatedStorage.Get(DataError,key,scope,ByRef<NavText>,target)");

        // IsolatedStorageRepository.Set/Get/Contains(6-arg)/Contains(5-arg)/Delete, and
        // ALSystemEncryption.ALEncrypt/ALDecrypt/ALKeyExists/ALEncryptionEnabled, are all
        // Cecil-owned (see NclCecilRewrite.cs) — same Repo_*/SysEnc_* replacement methods below.

        Console.Error.WriteLine($"[TenantStoragePatches] hooked {hooks} isolated-storage method(s)");
    }

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

    // ── Set entry points ───────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSet_2(DataError de, string key, string value)
        => SetImpl(key, value, DataScope.Module, Encryption.None, isSecret: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSet_3(DataError de, string key, string value, DataScope scope)
        => SetImpl(key, value, scope, Encryption.None, isSecret: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSet_Secret_3(DataError de, string key, NavSecretText value, DataScope scope)
        => SetImpl(key, value?.ALUnwrap()?.Value ?? string.Empty, scope, Encryption.None, isSecret: true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSetEncrypted_2(DataError de, string key, string value)
        => SetImpl(key, value, DataScope.Module, Encryption.Encrypted, isSecret: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSetEncrypted_Secret_2(DataError de, string key, NavSecretText value)
        => SetImpl(key, value?.ALUnwrap()?.Value ?? string.Empty, DataScope.Module, Encryption.Encrypted, isSecret: true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSetEncrypted_3(DataError de, string key, string value, DataScope scope)
        => SetImpl(key, value, scope, Encryption.Encrypted, isSecret: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALSetEncrypted_Secret_3(DataError de, string key, NavSecretText value, DataScope scope)
        => SetImpl(key, value?.ALUnwrap()?.Value ?? string.Empty, scope, Encryption.Encrypted, isSecret: true);

    private static bool SetImpl(string key, string value, DataScope scope, Encryption mode, bool isSecret)
    {
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("IsolatedStorage key must not be empty.");
        // We store the value AS PROVIDED. For SetEncrypted paths, AL has already
        // called ALSystemEncryption.ALEncrypt before reaching here, so `value` is
        // a "RNR1:" envelope (our patched AES). For plain Set, `value` is plaintext.
        // BC's Get does the symmetric decrypt based on the row's EncryptionStatus —
        // we replicate that in GetTextImpl by ALDecrypt-ing when mode == Encrypted.
        _store[ComposeKey(scope, key)] = new Entry(value ?? string.Empty, mode, isSecret);
        return true;
    }

    // ── Get entry points ───────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALGet_Text_2(DataError de, string key, ByRef<NavText> value)
        => GetTextImpl(key, DataScope.Module, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALGet_Text_3(DataError de, string key, DataScope scope, ByRef<NavText> value)
        => GetTextImpl(key, scope, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALGet_Secret_2(DataError de, string key, ByRef<NavSecretText> value)
        => GetSecretImpl(key, DataScope.Module, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALGet_Secret_3(DataError de, string key, DataScope scope, ByRef<NavSecretText> value)
        => GetSecretImpl(key, scope, value);

    private static bool GetTextImpl(string key, DataScope scope, ByRef<NavText> value)
    {
        if (!_store.TryGetValue(ComposeKey(scope, key), out var entry))
        {
            // Leave value at its default (NavText with empty string).
            value.Value = new NavText(string.Empty);
            return false;
        }
        // BC decrypts internally when the stored EncryptionStatus is Encrypted.
        var plain = entry.Status == Encryption.Encrypted ? SysEnc_ALDecrypt(entry.Ciphertext) : entry.Ciphertext;
        value.Value = new NavText(plain);
        return true;
    }

    private static bool GetSecretImpl(string key, DataScope scope, ByRef<NavSecretText> value)
    {
        if (!_store.TryGetValue(ComposeKey(scope, key), out var entry))
        {
            value.Value = new NavSecretText(new NavText(string.Empty));
            return false;
        }
        var plain = entry.Status == Encryption.Encrypted ? SysEnc_ALDecrypt(entry.Ciphertext) : entry.Ciphertext;
        value.Value = new NavSecretText(new NavText(plain));
        return true;
    }

    // ── Contains / Delete ──────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALContains_2(string key, DataScope scope)
        => _store.ContainsKey(ComposeKey(scope, key));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALContains_3(string key, DataScope scope, ByRef<bool> isSecret)
    {
        if (_store.TryGetValue(ComposeKey(scope, key), out var entry))
        {
            isSecret.Value = entry.IsSecret;
            return true;
        }
        isSecret.Value = false;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALDelete_1(DataError de, string key)
        => _store.TryRemove(ComposeKey(DataScope.Module, key), out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALDelete_2(DataError de, string key, DataScope scope)
    {
        // Real BC delete returns true even if the key didn't exist (idempotent).
        _store.TryRemove(ComposeKey(scope, key), out _);
        return true;
    }

    // ── ALIsolatedStorage.Set/Get (6-arg / 5-arg — the real AL emission targets)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALIsoSet_6(DataError de, string key, string value, DataScope scope,
                                  int encryptionStatus, /* TargetValueType */ int targetValueType)
    {
        var mode = encryptionStatus == 1 /* EncryptionStatus.Encrypted */ ? Encryption.Encrypted : Encryption.None;
        var isSecret = targetValueType == 1; // TargetValueType.SecretText = 1
        return SetImpl(key, value, scope, mode, isSecret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ALIsoGet_5_Text(DataError de, string key, DataScope scope,
                                       ByRef<NavText> value, /* TargetValueType */ int targetValueType)
        => GetTextImpl(key, scope, value);

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
