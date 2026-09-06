// RunnerFingerprintFileIdentityMemoTests — issue #3036.
//
// `.github/actions/provision-bc` populates `~/.al-runner/platform-apps` and then hard-links
// that tree into the default artifacts directory (`cp -al`, with a `cp -a` copy fallback).
// Program.cs folds both into the package-cache search set, so the SAME inodes are reached
// through two absolute paths — and a memo keyed on the path answers "never seen this file"
// for the second one, re-reading and re-hashing 122.5 MB of packages, Base Application's
// 98 MB among them, on every runner invocation.
//
// These tests pin BOTH directions, and the second one is the load-bearing half:
//
//   * dedup enough — two paths to ONE file are hashed once (`HardLink_*`, `Symlink_*`);
//   * dedup no further — two DISTINCT files stay distinct even when everything a stat can
//     see about them agrees (`DistinctFilesWithIdenticalSizeAndMtime_*`). A memo keyed on
//     (length, mtime), or on an inode number without the device, would serve one file's
//     hash for another file's bytes. That is a silently WRONG cache answer, strictly worse
//     than the duplicated work this issue is about.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// Clears process-wide static state (the shared hash memo) — joins the serial collection that
// every other class touching it already belongs to. It also reads the process-global
// computation counter as a delta and asserts an EXACT value, so a class hashing a file on
// another thread (ManifestDependencyEdgeScanTests, BcCompilerSharedReferenceMemoTests,
// CacheKeyDependencyContentIdentityTests) turns "2" into "3": reproduced at 3 failures in 14
// runs of a filter that co-schedules them, under xunit.runner.json's
// parallelizeTestCollections + maxParallelThreads 4.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class RunnerFingerprintFileIdentityMemoTests : IDisposable
{
    private readonly string _root;

    public RunnerFingerprintFileIdentityMemoTests()
    {
        // A fixture tree of our own — never the real shared package cache, which other
        // processes on this machine are using while these tests run. TestScratch, not a
        // hand-built temp path: ScratchDirs records an owner so a killed test host's tree is
        // reclaimed rather than leaked (ScratchDirOwnershipGuardTests enforces this).
        _root = TestScratch.Dir("al-runner-fileid-memo");
        Directory.CreateDirectory(_root);
        RunnerFingerprint.ClearFileContentHashMemoForTests();
    }

    public void Dispose()
    {
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>Hard-links <paramref name="target"/> at <paramref name="linkPath"/>, or
    /// returns false when the platform/filesystem cannot (Windows, cross-device, ...).</summary>
    private static bool TryHardLink(string target, string linkPath)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ln")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add(linkPath);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(30_000);
            return p.ExitCode == 0 && File.Exists(linkPath);
        }
        catch { return false; }
    }

    // ── dedup enough ────────────────────────────────────────────────────────

    [Fact]
    public void HardLinkedPaths_AreHashedOnce_AndAgreeOnTheHash()
    {
        var a = Write("pkg-a.app", System.Text.Encoding.UTF8.GetBytes("the same bytes, reached two ways"));
        var b = Path.Combine(_root, "pkg-b.app");
        var linked = TryHardLink(a, b);
        if (!OperatingSystem.IsLinux()) { if (!linked) return; }
        // On Linux this must not be a soft skip: a test that quietly proves nothing is how
        // a dedup fix silently stops applying without anything going red.
        else Assert.True(linked, "could not hard-link inside the fixture tree");

        var before = RunnerFingerprint.ContentHashComputationCountForTests;
        var ha = RunnerFingerprint.ComputeFileContentHashMemoized(a);
        var hb = RunnerFingerprint.ComputeFileContentHashMemoized(b);
        var computed = RunnerFingerprint.ContentHashComputationCountForTests - before;

        Assert.Equal(ha, hb);
        Assert.NotEqual(RunnerFingerprint.UnknownContentHash, ha);
        Assert.Equal(
            "e0a3d1a6e1f5b6ba0f0d5b6b5ed6f2b1e7a2b1c3d4e5f60718293a4b5c6d7e8f".Length,
            ha.Length); // a real SHA-256 hex digest, not a sentinel
        Assert.Equal(1, computed);
    }

    [Fact]
    public void SymlinkedPath_IsHashedOnce_BecauseItResolvesToTheSameFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = Write("real.app", System.Text.Encoding.UTF8.GetBytes("symlink target bytes"));
        var link = Path.Combine(_root, "link.app");
        File.CreateSymbolicLink(link, a);

        var before = RunnerFingerprint.ContentHashComputationCountForTests;
        var ha = RunnerFingerprint.ComputeFileContentHashMemoized(a);
        var hl = RunnerFingerprint.ComputeFileContentHashMemoized(link);
        var computed = RunnerFingerprint.ContentHashComputationCountForTests - before;

        Assert.Equal(ha, hl);
        Assert.Equal(1, computed);
    }

    // ── dedup no further: distinct files must stay distinct ─────────────────

    [Fact]
    public void DistinctFilesWithIdenticalContent_AreHashedSeparately()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("identical content, two separate files");
        var a = Write("twin-a.app", bytes);
        var b = Write("twin-b.app", bytes);

        var before = RunnerFingerprint.ContentHashComputationCountForTests;
        var ha = RunnerFingerprint.ComputeFileContentHashMemoized(a);
        var hb = RunnerFingerprint.ComputeFileContentHashMemoized(b);
        var computed = RunnerFingerprint.ContentHashComputationCountForTests - before;

        Assert.Equal(ha, hb); // same bytes really do hash the same
        Assert.Equal(2, computed); // but they are two files, and each was read
    }

    [Fact]
    public void DistinctFilesWithIdenticalSizeAndMtime_GetTheirOwnHashes()
    {
        // Everything a stat can see agrees: same length, same last-write time, same
        // directory. Only the bytes differ. A memo that keys on the stat rather than on
        // file identity answers the first file's hash for the second file's bytes.
        var a = Write("same-stat-a.app", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var b = Write("same-stat-b.app", new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 });
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, stamp);
        File.SetLastWriteTimeUtc(b, stamp);
        Assert.Equal(new FileInfo(a).Length, new FileInfo(b).Length);
        Assert.Equal(File.GetLastWriteTimeUtc(a), File.GetLastWriteTimeUtc(b));

        var before = RunnerFingerprint.ContentHashComputationCountForTests;
        var ha = RunnerFingerprint.ComputeFileContentHashMemoized(a);
        var hb = RunnerFingerprint.ComputeFileContentHashMemoized(b);
        var computed = RunnerFingerprint.ContentHashComputationCountForTests - before;

        Assert.NotEqual(ha, hb);
        Assert.Equal(2, computed);
        // And each answer is the real hash of ITS OWN bytes, not merely "not the other one".
        Assert.Equal(Sha256Hex(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), ha);
        Assert.Equal(Sha256Hex(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }), hb);
    }

    [Fact]
    public void MissingFile_AnswersUnknown_AndDoesNotPoisonThePathOnceItExists()
    {
        // A path that cannot be identified falls back to path keying and answers the
        // "unknown" sentinel. The file then appears — the later call must NOT inherit
        // that sentinel from the memo.
        var p = Path.Combine(_root, "later.app");
        Assert.Equal(RunnerFingerprint.UnknownContentHash, RunnerFingerprint.ComputeFileContentHashMemoized(p));

        var bytes = System.Text.Encoding.UTF8.GetBytes("now it exists");
        File.WriteAllBytes(p, bytes);
        Assert.Equal(Sha256Hex(bytes), RunnerFingerprint.ComputeFileContentHashMemoized(p));
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }
}
