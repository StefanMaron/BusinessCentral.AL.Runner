// AlCacheWriterTests — pins the atomic-publish contract for AL-output cache entries
// (issue #1810).
//
// Driving a REAL concurrent-reader interleaving against the filesystem is impractical
// to make deterministic in a unit test (the whole point of the bug is a timing window
// measured in microseconds). Instead these tests assert the temp-then-rename mechanics
// directly: the final path never observably exists mid-write, the temp file lives in the
// same directory as the final path (so the rename is same-volume and therefore atomic),
// and — the shape that matters for issue #1810 specifically — when a caller publishes a
// sidecar and then a DLL through two separate AtomicPublish calls (the pattern
// Program.cs now uses at both cache-write sites), the DLL only becomes visible after the
// sidecar is already in place. That is what makes AlCacheSidecars.IsCompleteEntry's
// "DLL present ⇒ every sidecar is already there" assumption true by construction.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlCacheWriterTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-cache-writer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AtomicPublish_FinalPathDoesNotExist_WhileWriteContentIsRunning()
    {
        var dir = NewTempDir();
        try
        {
            var finalPath = Path.Combine(dir, "entry.dll");
            bool finalPathExistedDuringWrite = true; // start "true" so a no-op write can't fake a pass

            AlCacheWriter.AtomicPublish(finalPath, tmp =>
            {
                finalPathExistedDuringWrite = File.Exists(finalPath);
                File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            });

            Assert.False(finalPathExistedDuringWrite);
            Assert.True(File.Exists(finalPath));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(finalPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AtomicPublish_TempFileLivesInSameDirectoryAsFinalPath()
    {
        var dir = NewTempDir();
        try
        {
            var finalPath = Path.Combine(dir, "entry.enum-registry.json");
            string? observedTmpDir = null;

            AlCacheWriter.AtomicPublish(finalPath, tmp =>
            {
                observedTmpDir = Path.GetDirectoryName(tmp);
                File.WriteAllText(tmp, "{}");
            });

            // Same directory ⇒ File.Move is a same-volume rename (atomic on Linux and
            // Windows). A temp directory elsewhere (e.g. Path.GetTempPath()) would risk a
            // cross-filesystem copy+delete instead, which is not atomic.
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(observedTmpDir!));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AtomicPublish_LeavesNoTempFileBehindOnSuccess()
    {
        var dir = NewTempDir();
        try
        {
            var finalPath = Path.Combine(dir, "entry.dll");
            AlCacheWriter.AtomicPublish(finalPath, tmp => File.WriteAllBytes(tmp, new byte[] { 9 }));

            var leftovers = Directory.GetFiles(dir).Where(f => !string.Equals(f, finalPath)).ToArray();
            Assert.Empty(leftovers);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AtomicPublish_OverwritesAnExistingCompleteEntry()
    {
        var dir = NewTempDir();
        try
        {
            var finalPath = Path.Combine(dir, "entry.dll");
            File.WriteAllBytes(finalPath, new byte[] { 0xDE, 0xAD });

            AlCacheWriter.AtomicPublish(finalPath, tmp => File.WriteAllBytes(tmp, new byte[] { 0xBE, 0xEF, 0xCA, 0xFE }));

            Assert.Equal(new byte[] { 0xBE, 0xEF, 0xCA, 0xFE }, File.ReadAllBytes(finalPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AtomicPublish_ReturnsWriteContentResult()
    {
        var dir = NewTempDir();
        try
        {
            var finalPath = Path.Combine(dir, "entry.enum-registry.json");
            int entries = AlCacheWriter.AtomicPublish(finalPath, tmp =>
            {
                File.WriteAllText(tmp, "{\"enums\":[]}");
                return 42;
            });
            Assert.Equal(42, entries);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The shape Program.cs uses at both cache-write sites (~1401, ~2373): sidecar
    // published first, DLL published last. This is the fix for #1810 — the DLL's
    // appearance is what AlCacheSidecars.IsCompleteEntry treats as "the entry is
    // usable", so publishing it last means a reader that sees the DLL is guaranteed to
    // also see the sidecar, and a reader that doesn't see the DLL correctly classifies
    // the entry as MISS regardless of what has happened to the sidecar.
    [Fact]
    public void SequencedPublish_SidecarThenDll_DllNeverVisibleBeforeSidecar()
    {
        var dir = NewTempDir();
        try
        {
            var sidecarPath = Path.Combine(dir, "abc123.enum-registry.json");
            var dllPath = Path.Combine(dir, "abc123.dll");

            bool sidecarExistedBeforeDllPublishStarted;

            AlCacheWriter.AtomicPublish(sidecarPath, tmp => File.WriteAllText(tmp, "{\"enums\":[]}"));

            // At this observation point (between the two AtomicPublish calls, exactly
            // where a concurrent reader could land) the sidecar is complete and the DLL
            // does not exist yet — AlCacheSidecars.IsCompleteEntry(dllExists:false, …)
            // correctly reports "not complete", so a reader here gets a clean MISS.
            sidecarExistedBeforeDllPublishStarted = File.Exists(sidecarPath);
            Assert.True(sidecarExistedBeforeDllPublishStarted);
            Assert.False(File.Exists(dllPath));

            AlCacheWriter.AtomicPublish(dllPath, tmp => File.WriteAllBytes(tmp, new byte[] { 1, 2, 3, 4 }));

            // Once the DLL is visible, the sidecar it depends on was already visible
            // first — never the other way around.
            Assert.True(File.Exists(sidecarPath));
            Assert.True(File.Exists(dllPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
