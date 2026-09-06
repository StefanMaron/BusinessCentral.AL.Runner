// FileIdentity — "are these two paths the same file?", answered by the filesystem rather
// than by comparing paths.
//
// Issue #3036. `.github/actions/provision-bc` populates `~/.al-runner/platform-apps` and
// then hard-links that tree into the default artifacts directory:
//
//     cp -al "$HOME/.al-runner/platform-apps/." "$DEFAULT_PLATFORM_APPS/" \
//       || cp -a "$HOME/.al-runner/platform-apps/." "$DEFAULT_PLATFORM_APPS/"
//
// Program.cs folds ProvisioningCheck.CollectRunnerOwnedProvisionDirs(...) into the
// package-cache search set and CI additionally passes `--package-cache
// "$HOME/.al-runner/platform-apps"`, so both directories are scanned — and they are the
// same inodes. Nothing about a path reveals that, so a memo keyed on the path re-reads and
// re-hashes 122.5 MB of packages (98 MB of it Base Application) on every invocation.
//
// ── The key, and why it cannot collide ─────────────────────────────────────────────────
//
// The key is (device, inode, size, mtime). All four, deliberately:
//
//   * An inode number is unique only WITHIN a device. Two package-cache roots on different
//     filesystems — `$HOME` on one mount, an artifacts directory on another, which is a
//     perfectly ordinary developer or CI layout — reach inode numbers from independent
//     numbering spaces, so an inode-only key would eventually declare two unrelated
//     packages "the same file" and serve one's content hash for the other's bytes.
//     Including the device makes the pair globally unique on a live filesystem.
//
//   * (device, inode) is unique among files that exist AT THE SAME TIME, and no further.
//     Delete a file and the kernel is free to hand its inode number to the next file
//     created — so within one long-lived process (a `--watch` run, or a run that unlinks a
//     staging package it wrote), a bare (device, inode) key CAN be asked about two
//     genuinely different files. Size and mtime bound that: a reused inode carrying
//     different content almost always differs in one of them, and the ONE case it does not
//     — a reused inode whose new file has the same length and was written inside the same
//     timestamp tick — is a strictly smaller window than the same-length-same-mtime
//     collision a stat-only key is exposed to for two files that both still exist.
//
// So the key never says "same file" for two files that exist at once, which is the only
// claim the memo above it makes. It is NOT a content identity and must never be persisted
// to disk or shared between processes: an inode number means nothing on another machine,
// and means something different on this one after the file is gone. In-process memo only.
//
// ── Platform ───────────────────────────────────────────────────────────────────────────
//
// .NET exposes no portable file-identity primitive (`FileInfo` has no inode;
// `ResolveLinkTarget` answers for symlinks only, not hard links), so this is a syscall.
// Linux `statx` was chosen over `stat`: its struct layout is defined by the kernel uapi and
// is identical on every architecture, whereas `struct stat`'s layout is libc- and
// arch-specific and glibc did not export a plain `stat` symbol before 2.33.
//
// Anywhere `statx` is unavailable — Windows, macOS, a libc without it — TryGetStableKey
// returns null and every caller falls back to path keying, i.e. exactly today's behaviour:
// duplicated work, never a wrong answer. The same is true of the `cp -a` fallback in the
// provisioning action above: if the hard link cannot be made (typically because the two
// directories are on different filesystems) the copy is a genuinely separate file with its
// own inode, this returns two different keys, and both copies are hashed — which is
// correct, because they really are two files that could drift apart. A future provisioning
// path that copies instead of linking therefore does not break anything here; it silently
// gives up the saving, and #3036's measurement is the thing to re-run if that happens.
using System.Runtime.InteropServices;

namespace AlRunner.Infrastructure;

internal static class FileIdentity
{
    /// <summary>
    /// A key identifying the FILE at <paramref name="fullPath"/> — equal for two paths that
    /// are hard links to one inode, different for two files that both exist — or null when
    /// this platform cannot answer, in which case callers must fall back to the path.
    ///
    /// <para>Symlinks are followed, so a symlink and its target share a key: they are the
    /// same bytes, which is the question the callers are asking.</para>
    ///
    /// <para>Never persist this. See the header for why it is meaningless off this machine
    /// and meaningless on it once the file is unlinked.</para>
    /// </summary>
    internal static string? TryGetStableKey(string fullPath)
    {
        if (!_statxUsable) return null;
        try
        {
            var buf = new StatxBuffer();
            // AT_FDCWD with an absolute path; flags 0 = follow symlinks. The mask is a
            // request, not a promise — the kernel reports what it actually filled in
            // stx_mask.
            const uint wanted = StatxIno | StatxSize | StatxMtime;
            var rc = statx(AT_FDCWD, fullPath, 0, wanted, ref buf);
            if (rc != 0) return null;
            // ALL THREE, not just the inode. Size and mtime are what bound inode reuse (see
            // the header), so a filesystem that reported an inode and declined the other two
            // would silently hand back `ino|maj:min:ino|0|0.0` — a key carrying none of the
            // anti-aliasing weight, for every file on that mount. Answering null instead
            // costs the dedup and keeps the fallback's guarantees. Defensive rather than
            // observed: the mask measured on this repo's filesystems is 0x9fff.
            if ((buf.Mask & wanted) != wanted) return null;
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"ino|{buf.DevMajor}:{buf.DevMinor}:{buf.Ino}|{buf.Size}|{buf.MtimeSec}.{buf.MtimeNsec}");
        }
        catch (EntryPointNotFoundException) { _statxUsable = false; return null; }
        catch (DllNotFoundException) { _statxUsable = false; return null; }
        catch (PlatformNotSupportedException) { _statxUsable = false; return null; }
        catch { return null; }
    }

    // Latched off the first time the P/Invoke itself proves unavailable, so a platform
    // without statx pays one failed bind rather than one per file. A per-call failure
    // (ENOENT, EACCES) is NOT a reason to latch — the next file may well be readable.
    private static volatile bool _statxUsable = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private const int AT_FDCWD = -100;
    private const uint StatxMtime = 0x00000040;
    private const uint StatxIno = 0x00000100;
    private const uint StatxSize = 0x00000200;

    // struct statx, uapi/linux/stat.h — 256 bytes, identical layout on every architecture.
    // Only the fields this type reads are declared; the rest is covered by Size.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(112)] public long MtimeSec;   // stx_mtime.tv_sec
        [FieldOffset(120)] public uint MtimeNsec;  // stx_mtime.tv_nsec
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
        int flags,
        uint mask,
        ref StatxBuffer statxbuf);
}
