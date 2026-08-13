// RunnerFingerprintTests — pins the runner-identity component every on-disk cache key
// carries (issue #1815).
//
// Finding 2: the old "runner:{mtime}:{length}" line changed on every CI rebuild even
// when the assembly's bytes didn't change, so a persisted cache MISSed 100% of the time.
// ContentHash must be a function of BYTES, not mtime.
//
// Finding 3: fixing finding 2 in isolation is a correctness regression. A content hash
// is IDENTICAL across every BC-version CI leg (same commit, same build), so a cache key
// built from content alone would let all 8 legs share one entry — a leg could then load
// AL output compiled against another BC version's symbols. WriteKeyLines must always
// emit both the content hash AND an explicit bc:<version> line, and two keys that agree
// on content but differ on BC version must differ.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerFingerprintTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "runner-fingerprint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Positive / finding 2: two files with byte-identical content but DIFFERENT mtimes
    /// hash to the SAME fingerprint. This is the actual bug from #1815 — a key built from
    /// File.GetLastWriteTimeUtc would differ here and could never hit across a CI rebuild.
    /// </summary>
    [Fact]
    public void ComputeContentHash_SameBytesDifferentMtime_ProducesEqualHash()
    {
        var dir = NewTempDir();
        try
        {
            var pathA = Path.Combine(dir, "runner-a.dll");
            var pathB = Path.Combine(dir, "runner-b.dll");
            var bytes = new byte[] { 0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; // fake "PE-ish" bytes
            File.WriteAllBytes(pathA, bytes);
            File.WriteAllBytes(pathB, bytes);

            // Force genuinely different mtimes (some filesystems truncate resolution to
            // 1s or 2s, so a "just write twice quickly" race wouldn't reliably differ).
            File.SetLastWriteTimeUtc(pathA, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(pathB, new DateTime(2026, 6, 15, 12, 34, 56, DateTimeKind.Utc));
            Assert.NotEqual(File.GetLastWriteTimeUtc(pathA), File.GetLastWriteTimeUtc(pathB));

            var hashA = RunnerFingerprint.ComputeContentHash(pathA);
            var hashB = RunnerFingerprint.ComputeContentHash(pathB);

            Assert.Equal(hashA, hashB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Also required: different runner CONTENT must still yield different keys —
    /// content-addressing must not degenerate into a constant.
    /// </summary>
    [Fact]
    public void ComputeContentHash_DifferentBytes_ProducesDifferentHash()
    {
        var dir = NewTempDir();
        try
        {
            var pathA = Path.Combine(dir, "runner-a.dll");
            var pathB = Path.Combine(dir, "runner-b.dll");
            File.WriteAllBytes(pathA, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(pathB, new byte[] { 1, 2, 3, 5 }); // one byte different

            var hashA = RunnerFingerprint.ComputeContentHash(pathA);
            var hashB = RunnerFingerprint.ComputeContentHash(pathB);

            Assert.NotEqual(hashA, hashB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A missing/empty location degrades to the documented "unknown" sentinel
    /// rather than throwing — mirrors the old code's "runner:unknown" fallback for a
    /// location-less (e.g. single-file-hosted) assembly.</summary>
    [Fact]
    public void ComputeContentHash_MissingFile_ReturnsUnknownSentinel()
    {
        Assert.Equal("unknown", RunnerFingerprint.ComputeContentHash(""));
        Assert.Equal("unknown", RunnerFingerprint.ComputeContentHash(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll")));
    }

    /// <summary>
    /// Negative / finding 3 (the regression guard): two keys built from the SAME content
    /// hash but DIFFERENT BC versions must differ. Without this, a future refactor could
    /// silently drop the bc: line and reintroduce cross-leg cache poisoning.
    /// </summary>
    [Fact]
    public void WriteKeyLines_SameContentDifferentBcVersion_ProducesDifferentKeyMaterial()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "runner.dll");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            var contentHash = RunnerFingerprint.ComputeContentHash(path);

            var linesV27 = new List<string>();
            var linesV28 = new List<string>();
            RunnerFingerprint.WriteKeyLines(linesV27.Add, contentHash, new Version(27, 0, 0, 0));
            RunnerFingerprint.WriteKeyLines(linesV28.Add, contentHash, new Version(28, 4, 0, 0));

            // Same content hash both times — isolates the difference to the BC version.
            Assert.Contains(linesV27, l => l == $"runner:{contentHash}");
            Assert.Contains(linesV28, l => l == $"runner:{contentHash}");
            Assert.NotEqual(string.Join("\n", linesV27), string.Join("\n", linesV28));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Positive companion: SAME content hash and SAME BC version produce IDENTICAL key
    /// material — the fix must not make the key noisier than it needs to be (a key that
    /// varies on every call would satisfy the negative test above while also destroying
    /// the cache it's supposed to serve).
    /// </summary>
    [Fact]
    public void WriteKeyLines_SameContentSameBcVersion_ProducesEqualKeyMaterial()
    {
        var contentHash = "deadbeef";
        var linesA = new List<string>();
        var linesB = new List<string>();
        RunnerFingerprint.WriteKeyLines(linesA.Add, contentHash, new Version(27, 5, 0, 0));
        RunnerFingerprint.WriteKeyLines(linesB.Add, contentHash, new Version(27, 5, 0, 0));

        Assert.Equal(string.Join("\n", linesA), string.Join("\n", linesB));
    }

    /// <summary>
    /// The parameterless overload must not silently key a cache entry to whatever BC
    /// version BcArtifacts' lazy latest-in-cache default happens to pick — that is
    /// exactly the finding-3 cross-leg poisoning this type exists to prevent, one call
    /// site earlier. Tested against the extracted pure guard (see
    /// <see cref="RunnerFingerprint.RequireBcVersionSelected"/>'s doc comment for why:
    /// BcArtifacts' real selection state is process-global and, once set by any other
    /// test in this shared xunit process, cannot be forced back to "unselected").
    /// </summary>
    [Fact]
    public void RequireBcVersionSelected_NotSelected_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RunnerFingerprint.RequireBcVersionSelected(isSelected: false));
        Assert.Contains("BC version not yet selected", ex.Message);
        Assert.Contains(nameof(RunnerFingerprint.WriteKeyLines), ex.Message);
    }

    /// <summary>Negative companion: once selected, the guard is a no-op.</summary>
    [Fact]
    public void RequireBcVersionSelected_Selected_DoesNotThrow()
    {
        var ex = Record.Exception(() => RunnerFingerprint.RequireBcVersionSelected(isSelected: true));
        Assert.Null(ex);
    }
}
