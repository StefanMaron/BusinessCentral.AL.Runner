// NclShadowLockedSourcePublishTests — #3364: publishing the shadow dir can fail on the
// SOURCE side, not the destination side. Windows refuses to rename a directory while any
// process holds an open handle to any file inside it, and an on-access AV scanner's plain
// FileShare.Read handle on the ~50 MB Ncl.dll the build has just written is enough. That
// raises IOException naming the source path with shadowDir still ABSENT — the case
// #2512's `when (Directory.Exists(shadowDir))` filter could not match, so it escaped
// PublishShadowDir, EnsureShadowDir and TryShadowReexec and killed the process (exit 82)
// before any test ran.
//
// The move is injected rather than provoked with a real file lock because a real lock only
// blocks a rename on Windows — POSIX rename(2) succeeds with open handles, so a
// handle-based test would be permanently green on the Linux CI legs and prove nothing
// there.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclShadowLockedSourcePublishTests
{
    private const string MarkerFileName = ".al-runner-shadow-source";
    private const string EntryDllName = "al-runner.dll";
    private const string NclFileName = "Microsoft.Dynamics.Nav.Ncl.dll";

    private static string NewTempDir(string label)
    {
        var dir = TestScratch.FlatDir($"ncl-shadow-locked-{label}-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteCompleteShadowDir(string dir, string origFull, byte[] dllBytes)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, EntryDllName), dllBytes);
        File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 4, 5, 6 });
        File.WriteAllText(Path.Combine(dir, "al-runner.deps.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), "{}");
        // Manifest then marker, last — same invariant the real build holds (#3559).
        NclShadowRuntime.WriteManifest(dir);
        File.WriteAllText(Path.Combine(dir, MarkerFileName), origFull);
    }

    /// <summary>Positive: the lock clears after a few attempts (the field case — an AV scan
    /// finishes in milliseconds). The rename must be RETRIED, with a delay between attempts,
    /// and the publish must then succeed normally. Before this fix the very first
    /// IOException escaped, so there was no second attempt at all.</summary>
    [Fact]
    public void PublishShadowDir_SourceLockedThenReleased_RetriesWithBackoffAndPublishes()
    {
        var root = NewTempDir("transient");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 9, 9, 9 });
            var shadowDir = Path.Combine(root, "key");

            var attempts = 0;
            var delays = new List<int>();
            var result = NclShadowRuntime.PublishShadowDir(
                tempDir, shadowDir, origFull,
                move: (src, dst) =>
                {
                    attempts++;
                    // Exactly the exception .NET raises for ERROR_ACCESS_DENIED on the
                    // SOURCE of a directory rename, message and all.
                    if (attempts <= 3) throw new IOException($"Access to the path '{src}' is denied.");
                    Directory.Move(src, dst);
                },
                sleep: delays.Add);

            Assert.Equal(shadowDir, result);
            Assert.Equal(4, attempts);
            Assert.Equal(3, delays.Count);
            Assert.All(delays, d => Assert.InRange(d, 1, 500));
            // Backoff, not a fixed spin: later waits are never shorter than earlier ones,
            // and at least one pair strictly grows.
            for (var i = 1; i < delays.Count; i++) Assert.True(delays[i] >= delays[i - 1]);
            Assert.True(delays[^1] > delays[0], "the delay must grow across attempts");

            Assert.False(Directory.Exists(tempDir), "temp dir must be consumed by the successful move");
            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadowDir, origFull));
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(shadowDir, EntryDllName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Negative: the lock never clears. The process must NOT die — that is the
    /// #3364 crash — and the returned directory must be one that actually exists and is
    /// complete, so the caller's re-exec has something to run. Returning the never-created
    /// shadowDir here is what produced "The application to execute does not exist".</summary>
    [Theory]
    [InlineData(true)]   // IOException — what MoveDirectory raises for ERROR_ACCESS_DENIED
    [InlineData(false)]  // UnauthorizedAccessException — the same refusal, other mapping
    public void PublishShadowDir_SourceLockedForever_DoesNotThrowAndRunsFromTheCompleteTempDir(bool ioException)
    {
        var root = NewTempDir(ioException ? "stuck-io" : "stuck-uae");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 7, 7, 7 });
            var shadowDir = Path.Combine(root, "key");

            var attempts = 0;
            var result = NclShadowRuntime.PublishShadowDir(
                tempDir, shadowDir, origFull,
                move: (src, _) =>
                {
                    attempts++;
                    if (ioException) throw new IOException($"Access to the path '{src}' is denied.");
                    throw new UnauthorizedAccessException($"Access to the path '{src}' is denied.");
                },
                sleep: _ => { });

            Assert.Equal(tempDir, result);
            Assert.True(attempts >= 20, $"every attempt must be spent before giving up, saw {attempts}");
            Assert.True(Directory.Exists(tempDir), "the fallback directory must survive, it is what runs");
            Assert.True(NclShadowRuntime.IsShadowDirComplete(tempDir, origFull));
            Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(Path.Combine(result, EntryDllName)));
            Assert.False(Directory.Exists(shadowDir), "nothing may be published under a name we never renamed to");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The source-side failure must not be confused with the destination-side one:
    /// when the rename fails BECAUSE a sibling published a complete dir in the meantime,
    /// the existing adopt path still wins — no retry loop, no fallback to tempDir.</summary>
    [Fact]
    public void PublishShadowDir_MoveFailsBecauseASiblingPublished_StillAdoptsTheWinner()
    {
        var root = NewTempDir("adopt");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 1, 1, 1 });
            var shadowDir = Path.Combine(root, "key");

            var attempts = 0;
            var result = NclShadowRuntime.PublishShadowDir(
                tempDir, shadowDir, origFull,
                move: (_, dst) =>
                {
                    attempts++;
                    // The sibling lands between our Exists check and our rename.
                    WriteCompleteShadowDir(dst, origFull, dllBytes: new byte[] { 2, 2, 2 });
                    throw new IOException("Cannot create a file when that file already exists.");
                },
                sleep: _ => throw new Xunit.Sdk.XunitException("must not back off when there is a winner to adopt"));

            Assert.Equal(shadowDir, result);
            Assert.Equal(1, attempts);
            Assert.False(Directory.Exists(tempDir), "our own build must be discarded");
            Assert.Equal(new byte[] { 2, 2, 2 }, File.ReadAllBytes(Path.Combine(shadowDir, EntryDllName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
