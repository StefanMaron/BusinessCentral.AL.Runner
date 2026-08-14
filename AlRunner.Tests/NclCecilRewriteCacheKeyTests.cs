// NclCecilRewriteCacheKeyTests — pins issue #1871's fix to NclCecilRewrite.ComputeCacheKey.
//
// Same defect family as #1815 (al-out) / #1820 (bc-symbols): the ncl-cecil cache key used
// to fold in `File.GetLastWriteTimeUtc(typeof(NclCecilRewrite).Assembly.Location).Ticks` —
// the RUNNER's own build-output mtime. CI rebuilds the runner fresh on every run, so that
// line changed on every run even when the runner's bytes (and therefore the Cecil rewrite
// it produces) were byte-for-byte identical to a prior run. A `ncl-cecil` cache entry
// persisted across CI runs (actions/cache) would therefore MISS unconditionally.
//
// Fix: replace the mtime line with RunnerFingerprint.ComputeContentHash of the runner
// assembly's own bytes — the same helper #1817/#1820 already use for this exact purpose.
//
// Positive: same Ncl bytes + same runner assembly BYTES, different runner assembly MTIMES
// -> equal keys.
// Negative: same Ncl bytes, different runner assembly BYTES -> different keys (guards
// against a fix that drops the runner-version dependency entirely rather than just
// de-mtime-ing it).
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclCecilRewriteCacheKeyTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncl-cecil-cachekey-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Positive: two "runner assembly" files with byte-identical content but DIFFERENT
    /// mtimes must fold into the SAME ncl-cecil cache key when paired with the same Ncl
    /// bytes. This is the actual #1871 bug — a key built from
    /// File.GetLastWriteTimeUtc(runnerAssembly).Ticks would differ here and could never
    /// hit across a CI rebuild, even though the rewritten output would be identical.
    /// </summary>
    [Fact]
    public void ComputeCacheKey_SameRunnerBytesDifferentMtime_ProducesEqualKey()
    {
        var dir = NewTempDir();
        try
        {
            var nclBytes = new byte[] { 0x4D, 0x5A, 10, 20, 30, 40, 50 }; // fake "PE-ish" Ncl bytes

            var runnerPathA = Path.Combine(dir, "al-runner-a.dll");
            var runnerPathB = Path.Combine(dir, "al-runner-b.dll");
            var runnerBytes = new byte[] { 0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8 };
            File.WriteAllBytes(runnerPathA, runnerBytes);
            File.WriteAllBytes(runnerPathB, runnerBytes);

            // Force genuinely different mtimes (some filesystems truncate resolution to
            // 1s or 2s, so a "just write twice quickly" race wouldn't reliably differ).
            File.SetLastWriteTimeUtc(runnerPathA, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(runnerPathB, new DateTime(2026, 6, 15, 12, 34, 56, DateTimeKind.Utc));
            Assert.NotEqual(File.GetLastWriteTimeUtc(runnerPathA), File.GetLastWriteTimeUtc(runnerPathB));

            var runnerContentHashA = RunnerFingerprint.ComputeContentHash(runnerPathA);
            var runnerContentHashB = RunnerFingerprint.ComputeContentHash(runnerPathB);

            var keyA = NclCecilRewrite.ComputeCacheKeyCore(nclBytes, runnerContentHashA);
            var keyB = NclCecilRewrite.ComputeCacheKeyCore(nclBytes, runnerContentHashB);

            Assert.Equal(keyA, keyB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Negative: same Ncl bytes, DIFFERENT runner assembly bytes (i.e. the Cecil-rewrite
    /// logic itself changed between runner builds) must still produce DIFFERENT keys.
    /// Guards against a fix that accidentally drops the runner-version dependency
    /// entirely rather than just de-mtime-ing it — which would let a stale rewrite from
    /// an old runner build survive a runner upgrade.
    /// </summary>
    [Fact]
    public void ComputeCacheKey_DifferentRunnerBytes_ProducesDifferentKey()
    {
        var dir = NewTempDir();
        try
        {
            var nclBytes = new byte[] { 0x4D, 0x5A, 10, 20, 30, 40, 50 };

            var runnerPathA = Path.Combine(dir, "al-runner-a.dll");
            var runnerPathB = Path.Combine(dir, "al-runner-b.dll");
            File.WriteAllBytes(runnerPathA, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(runnerPathB, new byte[] { 1, 2, 3, 5 }); // one byte different

            var runnerContentHashA = RunnerFingerprint.ComputeContentHash(runnerPathA);
            var runnerContentHashB = RunnerFingerprint.ComputeContentHash(runnerPathB);
            Assert.NotEqual(runnerContentHashA, runnerContentHashB);

            var keyA = NclCecilRewrite.ComputeCacheKeyCore(nclBytes, runnerContentHashA);
            var keyB = NclCecilRewrite.ComputeCacheKeyCore(nclBytes, runnerContentHashB);

            Assert.NotEqual(keyA, keyB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Companion negative: same runner content hash, DIFFERENT Ncl bytes must still
    /// produce different keys — the Ncl-content dependency the issue explicitly says is
    /// fine already must survive the fix untouched.
    /// </summary>
    [Fact]
    public void ComputeCacheKey_DifferentNclBytes_ProducesDifferentKey()
    {
        var runnerContentHash = "deadbeef";
        var keyA = NclCecilRewrite.ComputeCacheKeyCore(new byte[] { 1, 2, 3, 4 }, runnerContentHash);
        var keyB = NclCecilRewrite.ComputeCacheKeyCore(new byte[] { 1, 2, 3, 5 }, runnerContentHash);

        Assert.NotEqual(keyA, keyB);
    }
}
