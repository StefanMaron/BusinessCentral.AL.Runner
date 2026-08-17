// InstallBaselineDiskCache — the on-disk half of the #1867 dependency+company install-baseline
// cache. The in-memory dictionary in TestExecutor.Run removes the repeat inside one process;
// this removes it across processes, which is where the remaining cost lives (every CI test
// spawn, every corpus/runner-extras invocation, every --watch rebuild pays the same 5.9s).
//
// Layout: <cache-root>/install-baseline/<sha256-of-key>.bin, written atomically (temp file in
// the same directory + File.Move(overwrite)) because CI runs four runner processes in
// parallel and they all key on the same MS-platform dependency closure.
//
// The KEY is everything the snapshot's content depends on:
//   * the dependency assembly set, by Module Version ID (InstallTriggerRunner
//     .CurrentDependencySetKey) — MVIDs move whenever the underlying IL does, so a dependency
//     recompiled after a source edit re-keys instead of hitting a stale entry. This also
//     covers the dependency .app bytes: what is loaded and fires Install triggers is the DLL
//     extracted from the .app, and its MVID is that DLL's identity.
//   * the runner build + selected BC version (RunnerFingerprint.WriteKeyLines) — the snapshot
//     is produced by runner patch code executing BC bodies, so both sides can change it
//     without any dependency changing.
//   * the codec's schema version — a layout change must not read an old file.
//
// Failure policy: every disk operation is best-effort. A read that cannot produce a valid
// snapshot deletes the entry and reports a miss; a write that fails logs and is dropped. The
// caller always has the fresh-computation path available, so the cache can never be the
// reason a run fails — but every fallback is logged (never silent).
namespace AlRunner.Infrastructure;

internal static class InstallBaselineDiskCache
{
    internal const string CacheName = "install-baseline";

    /// <summary>Kill switch shared with the in-memory cache: forces a full recompute and skips
    /// BOTH the disk read and the disk write, so the fresh-computation path can be re-run on
    /// demand without a patched rebuild and without a poisoned file surviving the diagnosis.</summary>
    internal static bool Disabled
        => Environment.GetEnvironmentVariable("AL_RUNNER_NO_DEP_COMPANY_CACHE") == "1";

    /// <summary>The full, human-readable key text. Hashed for the filename, and embedded in the
    /// file itself so a hash collision or a hand-copied file cannot be mistaken for a hit.</summary>
    internal static string BuildKeyText(string dependencySetKey, int schemaVersion)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("install-baseline-schema:").Append(schemaVersion).Append('\n');
        RunnerFingerprint.WriteKeyLines(line => sb.Append(line).Append('\n'));
        sb.Append("deps:").Append(dependencySetKey).Append('\n');
        return sb.ToString();
    }

    internal static string HashKey(string keyText)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(keyText)))
            .ToLowerInvariant();
    }

    internal static string PathForKey(string keyText)
        => Path.Combine(CacheRoots.Resolve(CacheName), HashKey(keyText) + ".bin");

    /// <summary>Read the raw bytes for a key, or null when there is no usable entry. Deletes an
    /// entry it could not read so the next run rewrites it instead of failing again.</summary>
    internal static byte[]? TryRead(string keyText)
    {
        var path = PathForKey(keyText);
        // Logged unconditionally (verbose-gated by Log.cs's [Component] filter) on every
        // lookup: "which file did this run consult" is the first question of any cache
        // investigation, and it is also how the cross-process tests locate the entry they
        // then corrupt or diff.
        Console.Error.WriteLine($"[InstallBaselineDisk] entry path: {path}");
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InstallBaselineDisk] unreadable entry {path}: {ex.GetType().Name}: {ex.Message}");
            Delete(keyText);
            return null;
        }
    }

    /// <summary>Delete the entry for a key. Used when the bytes are present but do not decode —
    /// a corrupt or truncated file (a process killed mid-write on a filesystem without atomic
    /// rename, say) must not make every subsequent run pay the decode failure.</summary>
    internal static void Delete(string keyText)
    {
        try { File.Delete(PathForKey(keyText)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InstallBaselineDisk] could not delete stale entry: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Write the entry atomically. A temp file in the SAME directory keeps the rename
    /// on one filesystem, and File.Move(overwrite: true) is the atomic replace — so a reader in
    /// a parallel CI process sees either the previous complete file or the new complete file,
    /// never a half-written one.</summary>
    internal static bool TryWrite(string keyText, byte[] payload)
    {
        var path = PathForKey(keyText);
        var dir = Path.GetDirectoryName(path)!;
        var tmp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(tmp, payload);
            File.Move(tmp, path, overwrite: true);
            Console.Error.WriteLine($"[InstallBaselineDisk] wrote {payload.Length} byte(s) to {path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InstallBaselineDisk] could not write entry {path}: {ex.GetType().Name}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }
    }
}
