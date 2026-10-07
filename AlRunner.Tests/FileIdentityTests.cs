// FileIdentityTests — issue #3036. Pins the identity key itself, independently of the memo
// that consumes it (RunnerFingerprintFileIdentityMemoTests).
//
// The key is read out of a hand-declared `struct statx` layout, so the first thing worth
// proving is that the offsets are actually right and not accidentally plausible: the
// device, inode and size this type reports are cross-checked against coreutils `stat`,
// which reads the same kernel structure through libc. An offset that were wrong by a field
// would still produce a stable-looking key, and every dedup test above would still pass.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class FileIdentityTests : IDisposable
{
    private readonly string _root;

    public FileIdentityTests()
    {
        // TestScratch, not a hand-built temp path — see ScratchDirOwnershipGuardTests.
        _root = TestScratch.Dir("al-runner-fileid");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string? Run(string exe, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch { return null; }
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void Key_ReportsTheSameDeviceInodeAndSizeAsCoreutilsStat()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("probe.bin", new byte[1234]);

        // Deliberately NOT a soft skip. A statx that stopped answering on Linux is the fix
        // in #3036 silently ceasing to apply, which is exactly the thing a test must shout
        // about rather than pass over.
        var key = FileIdentity.TryGetStableKey(path);
        Assert.NotNull(key);

        // %Hd major, %Ld minor, %i inode, %s size — the same four fields, via libc.
        var expected = Run("stat", "-c", "%Hd %Ld %i %s", path);
        Assert.NotNull(expected); // coreutils stat is the oracle; without it this proves nothing
        var f = expected!.Split(' ');
        Assert.Equal(4, f.Length);
        // The first three components: device, inode, size (mtime and ctime follow).
        Assert.Equal($"ino|{f[0]}:{f[1]}:{f[2]}|{f[3]}", string.Join('|', key!.Split('|')[..3]));
    }

    [Fact]
    public void Key_IsEqualForHardLinksAndDifferentForTwoFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var a = Write("a.bin", new byte[] { 1, 2, 3 });
        var b = Path.Combine(_root, "b.bin");
        Assert.NotNull(Run("ln", a, b)); // no hard link, nothing to prove — fail, do not skip
        var c = Write("c.bin", new byte[] { 1, 2, 3 }); // same bytes, its own inode

        var ka = FileIdentity.TryGetStableKey(a);
        var kb = FileIdentity.TryGetStableKey(b);
        var kc = FileIdentity.TryGetStableKey(c);
        Assert.NotNull(ka);

        Assert.Equal(ka, kb);
        Assert.NotEqual(ka, kc);
    }

    [Fact]
    public void Key_CarriesTheDevice_SoInodesFromTwoFilesystemsCannotCollide()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Inode numbers are unique only within a device, and a package cache can span two
        // mounts. Prove the device is actually IN the key by finding a second filesystem
        // and showing the device component differs — not by inspecting the source.
        var here = Write("here.bin", new byte[] { 9 });
        string? other = null;
        foreach (var dir in new[] { "/dev/shm", "/run/shm" })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var cand = Path.Combine(dir, "al-runner-fileid-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(cand, new byte[] { 9 });
                other = cand;
                break;
            }
            catch { }
        }
        // /dev/shm is a tmpfs on every Linux this runs on, including GitHub's runners, so
        // "no second filesystem" is a broken environment rather than a reason to pass.
        Assert.NotNull(other);

        try
        {
            var k1 = FileIdentity.TryGetStableKey(here);
            var k2 = FileIdentity.TryGetStableKey(other);
            Assert.NotNull(k1);
            Assert.NotNull(k2);
            var dev1 = string.Join(':', k1!.Split('|')[1].Split(':')[..2]);
            var dev2 = string.Join(':', k2!.Split('|')[1].Split(':')[..2]);
            Assert.NotEqual(dev1, dev2);
            // And the two files really are distinct despite identical content and length —
            // the device is what makes that survivable if their inode numbers ever agree.
            Assert.NotEqual(k1, k2);
        }
        finally { try { File.Delete(other!); } catch { } }
    }

    [Fact]
    public void Key_ChangesWhenTheFileIsRewritten_SoAReusedInodeCannotServeStaleContent()
    {
        if (!OperatingSystem.IsLinux()) return;
        var p = Write("rewritten.bin", new byte[] { 1, 2, 3 });
        var before = FileIdentity.TryGetStableKey(p);
        Assert.NotNull(before);

        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4, 5 }); // same inode, new size
        var after = FileIdentity.TryGetStableKey(p);
        Assert.NotEqual(before, after);
    }

    // #5409: mtime is writable from user space; ctime is not. Each fact keeps its own bytes and
    // length so no fact can meet another's file through a reused inode.
    [Fact]
    public void Key_ChangesOnAnInPlaceRewrite_EvenWithTheSameSizeAndTheMtimeRestored()
    {
        if (!OperatingSystem.IsLinux()) return;
        var mtime = new DateTime(2026, 1, 1, 0, 0, 7, DateTimeKind.Utc);
        var p = Write("inplace.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        File.SetLastWriteTimeUtc(p, mtime);
        var before = FileIdentity.TryGetStableKey(p);
        Assert.NotNull(before);

        Thread.Sleep(30); // beyond one kernel timestamp tick
        File.WriteAllBytes(p, new byte[] { 7, 6, 5, 4, 3, 2, 1 }); // same inode, same length
        File.SetLastWriteTimeUtc(p, mtime);
        var after = FileIdentity.TryGetStableKey(p);
        Assert.NotNull(after);

        // The key as it was before ctime joined it: device, inode, size and mtime all agree ...
        Assert.Equal(string.Join('|', before!.Split('|')[..4]), string.Join('|', after!.Split('|')[..4]));
        // ... and ctime is what tells the two files apart.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Key_IsUnchangedByAReadAndMovedByAMetadataChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        var p = Write("readonly.bin", new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
        var before = FileIdentity.TryGetStableKey(p);
        Thread.Sleep(30);
        _ = File.ReadAllBytes(p); // what hashing does
        Assert.Equal(before, FileIdentity.TryGetStableKey(p));

        // A chmod changes no byte and does move ctime, so it costs one re-hash and never a wrong answer.
        Thread.Sleep(30);
        File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.NotEqual(before, FileIdentity.TryGetStableKey(p));
    }

    [Fact]
    public void KeyFromStatx_AnAnswerWithoutCtime_IsDeclinedNotKeyedWeaker()
    {
        const uint ino = 0x100, size = 0x200, mtime = 0x40, ctime = 0x80;
        var full = FileIdentity.KeyFromStatx(ino | size | mtime | ctime, 1, 2, 3, 4, 5, 6, 7, 8);
        Assert.False(full.Declined);
        Assert.Equal("ino|1:2:3|4|5.6|7.8", full.Key);

        // statx answered, but not ctime: no key, and the caller must not fall back to a weaker one.
        Assert.Equal((null, true), FileIdentity.KeyFromStatx(ino | size | mtime, 1, 2, 3, 4, 5, 6, 7, 8));
        // Not answering the identity at all keeps the documented path fallback.
        Assert.Equal((null, false), FileIdentity.KeyFromStatx(size | mtime | ctime, 1, 2, 3, 4, 5, 6, 7, 8));
    }

    [Fact]
    public void Key_IsNull_ForAPathThatDoesNotExist()
    {
        Assert.Null(FileIdentity.TryGetStableKey(Path.Combine(_root, "absent.bin")));
    }

    [Fact]
    public void Key_IsNull_OnAPlatformWithoutStatx()
    {
        // Not a claim about Linux — a claim about the contract every caller relies on:
        // "cannot answer" is null, never a fabricated key. On a non-Linux box this is the
        // real assertion; on Linux the missing-file case above covers the same contract.
        if (OperatingSystem.IsLinux()) return;
        Assert.Null(FileIdentity.TryGetStableKey(Write("x.bin", new byte[] { 1 })));
    }
}
