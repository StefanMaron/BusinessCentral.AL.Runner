// RunnerFingerprintContentHashMemoTests — issue #2987.
//
// RunnerFingerprint.ComputeFileContentHashMemoized is the ONE memo every layer that identifies
// a file by its content shares: BcAppSymbolCache (#1820), the AL-output key's dependency terms
// (#2847), the r2r-chunks cache (#2955) and — since #2987 — AppLoader's persisted app-manifests
// index. It was keyed on the full path alone, on the premise that "nothing in this process
// writes to a dependency .app".
//
// That premise does not hold for every caller. InProcessAppPackager writes synthetic .app
// packages mid-run, and a --watch process outlives a rebuild of one. A path-keyed memo answers
// the FIRST bytes it ever saw for those, so a caller keying a PERSISTED cache on it would
// consult the previous package's entry — the wrong-answer shape content addressing removes,
// reintroduced one layer down. Concretely: it is what would have made
// AppLoaderManifestCacheTests.ReadManifest_TouchedMtime_IsReparsedNotServedStale start serving
// V1's manifest for V2's package once the index went content-keyed.
//
// The memo key is now (full path, length, last-write UTC). The two arms below pin both halves
// of what that does and does not buy — including, deliberately, the case it still cannot see,
// so nobody reads this memo as a content check.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// Clears process-wide static state (the shared hash memo) — joins the serial collection that
// every other class touching it already belongs to.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class RunnerFingerprintContentHashMemoTests
{
    private static string NewFile(string name, byte[] content, DateTime mtimeUtc)
    {
        var dir = TestScratch.FlatDir("runner-fingerprint-memo-tests-");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    /// <summary>
    /// A file rewritten in place with a moved mtime must hash as its NEW bytes, without anyone
    /// clearing the memo. This is the property #2987 added and the one AppLoader's on-disk
    /// index now depends on: the memo feeds a persisted cache key, so a stale answer here is a
    /// stale entry there.
    /// </summary>
    [Fact]
    public void RewrittenInPlaceWithAMovedMtime_HashesTheNewBytes()
    {
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try
        {
            var path = NewFile("pkg.bin", "version-one"u8.ToArray(),
                new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var first = RunnerFingerprint.ComputeFileContentHashMemoized(path);
            Assert.Equal(RunnerFingerprint.ComputeContentHash(path), first);
            Assert.NotEqual(RunnerFingerprint.UnknownContentHash, first);

            File.WriteAllBytes(path, "version-two-is-longer"u8.ToArray());
            File.SetLastWriteTimeUtc(path, new DateTime(2021, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var second = RunnerFingerprint.ComputeFileContentHashMemoized(path);
            Assert.NotEqual(first, second);
            // Not merely "different" — it is exactly what a fresh process would compute.
            Assert.Equal(RunnerFingerprint.ComputeContentHash(path), second);
        }
        finally { RunnerFingerprint.ClearFileContentHashMemoForTests(); }
    }

    /// <summary>
    /// The limit, stated rather than left to be discovered: a rewrite that lands on the SAME
    /// length and the SAME mtime is invisible to the memo, which keeps answering the bytes it
    /// first saw. That is what makes this a memo and not a content check — and it is precisely
    /// why the PERSISTED keys built from this value, which outlive the process, are the ones
    /// that have to be right (#2987, #2955).
    ///
    /// <para>It is also a proving assertion in the other direction: an implementation that
    /// re-read the file on every call would fail here, so this pins that the memoization is
    /// real and not accidentally removed.</para>
    /// </summary>
    [Fact]
    public void RewrittenInPlaceOntoTheSameLengthAndMtime_KeepsTheMemoizedAnswer()
    {
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try
        {
            var stamp = new DateTime(2022, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var path = NewFile("pkg.bin", "aaaaaaaaaaaa"u8.ToArray(), stamp);

            var memoized = RunnerFingerprint.ComputeFileContentHashMemoized(path);

            var replacement = "bbbbbbbbbbbb"u8.ToArray();
            Assert.Equal(new FileInfo(path).Length, replacement.Length); // the collision, asserted
            File.WriteAllBytes(path, replacement);
            File.SetLastWriteTimeUtc(path, stamp);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

            // The memo answers the old bytes...
            Assert.Equal(memoized, RunnerFingerprint.ComputeFileContentHashMemoized(path));
            // ...and the unmemoized computation shows they really did change, so this is the
            // memo's blind spot rather than two identical files.
            Assert.NotEqual(memoized, RunnerFingerprint.ComputeContentHash(path));

            // Clearing it — what a fresh process does implicitly — recovers the new answer.
            RunnerFingerprint.ClearFileContentHashMemoForTests();
            Assert.Equal(RunnerFingerprint.ComputeContentHash(path),
                         RunnerFingerprint.ComputeFileContentHashMemoized(path));
        }
        finally { RunnerFingerprint.ClearFileContentHashMemoForTests(); }
    }

    /// <summary>Two files with different bytes never share a memo entry, and a missing file
    /// answers the sentinel rather than throwing or inheriting a neighbour's hash.</summary>
    [Fact]
    public void DistinctFilesGetDistinctHashes_AndAMissingFileGetsTheSentinel()
    {
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try
        {
            var stamp = new DateTime(2023, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var a = NewFile("a.bin", "alpha"u8.ToArray(), stamp);
            var b = NewFile("b.bin", "bravo"u8.ToArray(), stamp);

            var hashA = RunnerFingerprint.ComputeFileContentHashMemoized(a);
            var hashB = RunnerFingerprint.ComputeFileContentHashMemoized(b);
            Assert.NotEqual(hashA, hashB);
            Assert.Equal(64, hashA.Length);
            Assert.Equal(RunnerFingerprint.ComputeContentHash(a), hashA);
            Assert.Equal(RunnerFingerprint.ComputeContentHash(b), hashB);

            var missing = Path.Combine(Path.GetDirectoryName(a)!, "not-there.bin");
            Assert.Equal(RunnerFingerprint.UnknownContentHash,
                         RunnerFingerprint.ComputeFileContentHashMemoized(missing));

            // And once it exists, the sentinel is not what it inherits — the stat that failed
            // memoized under the bare path, so a successful stat is a different key.
            File.WriteAllBytes(missing, "charlie"u8.ToArray());
            var appeared = RunnerFingerprint.ComputeFileContentHashMemoized(missing);
            Assert.NotEqual(RunnerFingerprint.UnknownContentHash, appeared);
            Assert.Equal(RunnerFingerprint.ComputeContentHash(missing), appeared);
        }
        finally { RunnerFingerprint.ClearFileContentHashMemoForTests(); }
    }
}
