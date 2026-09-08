// NclShadowInUseAndManifestTests — #3559.
//
// Measured mechanism (full account: docs/ncl-shadow-runtime.md). A sibling runner's
// PruneStaleShadowDirs deleted a PUBLISHED shadow dir that another process was executing
// from. On Windows the recursive delete removes every file the victim does not hold mapped
// and leaves the rest, so the directory survives holding only its loaded assemblies — 22 of
// 452 files in the field report — and the victim dies twenty minutes later on the first file
// it opens by path (Roslyn, resolving a metadata reference).
//
// Two guards, pinned here: a published dir carries a manifest of its whole file set and is
// refused when anything it lists is absent, and a dir whose in-use lock is held open is never
// pruned.
using System.Runtime.InteropServices;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclShadowInUseAndManifestTests
{
    private const string MarkerFileName = ".al-runner-shadow-source";
    private const string ManifestFileName = ".al-runner-shadow-manifest";
    private const string EntryDllName = "al-runner.dll";
    private const string NclFileName = "Microsoft.Dynamics.Nav.Ncl.dll";
    private const string OrigFull = @"C:\install\any";

    private static string NewRoot(string label)
    {
        var dir = TestScratch.FlatDir($"ncl-shadow-3559-{label}-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A shadow dir as a completed mirror leaves it: the launch set, a handful of
    /// ordinary dependency files including one in a nested directory, a manifest listing all
    /// of them, and the marker last.</summary>
    private static void WriteCompleteShadowDir(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, EntryDllName), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 4, 5, 6 });
        File.WriteAllText(Path.Combine(dir, "al-runner.deps.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), "{}");
        File.WriteAllBytes(Path.Combine(dir, "Newtonsoft.Json.dll"), new byte[] { 7 });
        Directory.CreateDirectory(Path.Combine(dir, "runtimes", "win", "lib", "net8.0"));
        File.WriteAllBytes(Path.Combine(dir, "runtimes", "win", "lib", "net8.0", "System.Diagnostics.EventLog.Messages.dll"),
            new byte[] { 9 });
        NclShadowRuntime.WriteManifest(dir);
        File.WriteAllText(Path.Combine(dir, MarkerFileName), OrigFull);
    }

    // ── The manifest ────────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_ListsEveryMirroredFile_AndNoBookkeepingFile()
    {
        var root = NewRoot("manifest-contents");
        try
        {
            WriteCompleteShadowDir(root);
            var entries = File.ReadAllLines(Path.Combine(root, ManifestFileName));

            Assert.Contains(EntryDllName, entries);
            Assert.Contains("Newtonsoft.Json.dll", entries);
            Assert.Contains("runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll", entries);
            Assert.DoesNotContain(ManifestFileName, entries);
            Assert.DoesNotContain(MarkerFileName, entries);
            Assert.Equal(6, entries.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The field case exactly: the launch set survives (mapped by the running
    /// process) and an ordinary dependency file does not. Every pre-#3559 check passes on
    /// this directory; the manifest is what refuses it, and it names the file.</summary>
    [Fact]
    public void IsShadowDirComplete_ManifestListsAnAbsentFile_RefusesAndNamesIt()
    {
        var root = NewRoot("manifest-missing-file");
        try
        {
            WriteCompleteShadowDir(root);
            var victim = Path.Combine(root, "runtimes", "win", "lib", "net8.0",
                "System.Diagnostics.EventLog.Messages.dll");
            File.Delete(victim);

            Assert.False(NclShadowRuntime.IsShadowDirComplete(root, OrigFull),
                "a dir missing a file its own manifest lists must not be offered to a run");

            var missing = string.Join(", ", NclShadowRuntime.MissingRequiredNames(root, OrigFull));
            Assert.Contains("runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll", missing);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsShadowDirComplete_NoManifest_Refuses()
    {
        var root = NewRoot("manifest-absent");
        try
        {
            WriteCompleteShadowDir(root);
            File.Delete(Path.Combine(root, ManifestFileName));

            Assert.False(NclShadowRuntime.IsShadowDirComplete(root, OrigFull));
            Assert.Contains("(no manifest)", string.Join(", ", NclShadowRuntime.MissingRequiredNames(root, OrigFull)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Positive control for the two negatives above — the same fixture, untouched,
    /// is accepted. Without this the refusals could pass with a check that refuses everything.</summary>
    [Fact]
    public void IsShadowDirComplete_WholeDir_IsAccepted()
    {
        var root = NewRoot("manifest-complete");
        try
        {
            WriteCompleteShadowDir(root);
            Assert.True(NclShadowRuntime.IsShadowDirComplete(root, OrigFull));
            Assert.Empty(NclShadowRuntime.MissingRequiredNames(root, OrigFull));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A dangling symlink is a missing file (#2166's shape) — File.Exists says so,
    /// and the manifest turns that into a refusal instead of a load failure hours later.</summary>
    [Fact]
    public void IsShadowDirComplete_ManifestEntryIsADanglingSymlink_Refuses()
    {
        var root = NewRoot("manifest-dangling");
        var target = Path.Combine(root, "target.dll");
        var shadow = Path.Combine(root, "shadow");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(target, new byte[] { 1 });
            WriteCompleteShadowDir(shadow);
            try { File.CreateSymbolicLink(Path.Combine(shadow, "Linked.dll"), target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // symlinks refused on this box (the very condition #3559 was measured under)
            }
            NclShadowRuntime.WriteManifest(shadow);
            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadow, OrigFull));

            File.Delete(target);
            Assert.False(NclShadowRuntime.IsShadowDirComplete(shadow, OrigFull));
            Assert.Contains("Linked.dll", string.Join(", ", NclShadowRuntime.MissingRequiredNames(shadow, OrigFull)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── The in-use lock ─────────────────────────────────────────────────────────

    [Fact]
    public void InUseLock_HeldMeansInUse_ReleasedMeansNot()
    {
        var root = NewRoot("lock-roundtrip");
        try
        {
            Assert.False(NclShadowRuntime.IsInUse(root), "an empty dir is not in use");
            var held = NclShadowRuntime.AcquireInUseLock(root);
            Assert.NotNull(held);
            Assert.True(NclShadowRuntime.IsInUse(root), "a held lock must read as in use");
            held!.Dispose();
            Assert.False(NclShadowRuntime.IsInUse(root),
                "a lock file left behind by a dead process must not protect the dir forever");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The #3559 regression pin: the oldest dir is prune fodder by every rule the
    /// prune had, and is being executed from. It must survive with every file intact.</summary>
    [Fact]
    public void PruneStaleShadowDirs_NeverDeletesADirWhoseInUseLockIsHeld()
    {
        var root = NewRoot("prune-skips-live");
        FileStream? held = null;
        try
        {
            var live = Path.Combine(root, "published-live");
            WriteCompleteShadowDir(live);
            Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddDays(-30));
            held = NclShadowRuntime.AcquireInUseLock(live);
            Assert.NotNull(held);

            for (var i = 0; i < 3; i++)
            {
                var d = Path.Combine(root, $"published-{i}");
                Directory.CreateDirectory(d);
                Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddHours(-1 - i));
            }
            var protectedDir = Path.Combine(root, "protected");
            Directory.CreateDirectory(protectedDir);

            NclShadowRuntime.PruneStaleShadowDirs(root, protectedDir, keepNewest: 2);

            Assert.True(Directory.Exists(live), "a shadow dir a live process is executing from must survive a prune");
            Assert.True(File.Exists(Path.Combine(live, "runtimes", "win", "lib", "net8.0",
                    "System.Diagnostics.EventLog.Messages.dll")),
                "a partially-emptied survivor is the #3559 defect — the dir must be intact, not merely present");
            Assert.True(NclShadowRuntime.IsShadowDirComplete(live, OrigFull));
        }
        finally
        {
            held?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Negative: the lock must not become "prune never deletes anything". An old dir
    /// whose lock file is a leftover from a dead process is still reclaimed.</summary>
    [Fact]
    public void PruneStaleShadowDirs_StaleUnheldLockFile_StillPrunes()
    {
        var root = NewRoot("prune-stale-lock");
        try
        {
            var stale = Path.Combine(root, "published-stale");
            WriteCompleteShadowDir(stale);
            File.WriteAllText(Path.Combine(stale, NclShadowRuntime.InUseLockPrefix + "999999"), "pid 999999");
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

            for (var i = 0; i < 3; i++)
            {
                var d = Path.Combine(root, $"published-{i}");
                Directory.CreateDirectory(d);
                Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddHours(-1 - i));
            }
            var protectedDir = Path.Combine(root, "protected");
            Directory.CreateDirectory(protectedDir);

            NclShadowRuntime.PruneStaleShadowDirs(root, protectedDir, keepNewest: 2);

            Assert.False(Directory.Exists(stale),
                "an unheld lock file is not a claim — the dir must still be reclaimable");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The age floor, the second guard: a freshly published dir is not pruned even
    /// with no lock on it, which covers a process built before the lock existed.</summary>
    [Fact]
    public void PruneStaleShadowDirs_RecentlyPublishedDir_IsNotPruned()
    {
        var root = NewRoot("prune-age-floor");
        try
        {
            var recent = Path.Combine(root, "published-recent");
            WriteCompleteShadowDir(recent);
            Directory.SetLastWriteTimeUtc(recent, DateTime.UtcNow - NclShadowRuntime.MinPruneAge + TimeSpan.FromMinutes(1));

            for (var i = 0; i < 4; i++)
            {
                var d = Path.Combine(root, $"published-{i}");
                Directory.CreateDirectory(d);
                Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddMinutes(-5));
            }
            var protectedDir = Path.Combine(root, "protected");
            Directory.CreateDirectory(protectedDir);

            NclShadowRuntime.PruneStaleShadowDirs(root, protectedDir, keepNewest: 2);

            Assert.True(Directory.Exists(recent));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The Windows behaviour the whole issue rests on, measured rather than assumed:
    /// a recursive delete over a directory with one open FileShare.Read handle throws, removes
    /// every other file, and leaves the held one — the 22-of-452 signature. Windows-only; on
    /// Linux unlink succeeds and the survivor set is empty, which is why the prune has to
    /// probe before deleting instead of relying on the delete failing.</summary>
    [Fact]
    public void RecursiveDeleteOverAnOpenHandle_LeavesOnlyTheHeldFile_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var root = NewRoot("delete-signature");
        try
        {
            WriteCompleteShadowDir(root);
            using (var held = File.Open(Path.Combine(root, EntryDllName), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => Directory.Delete(root, recursive: true));
                Assert.True(File.Exists(Path.Combine(root, EntryDllName)), "the mapped file survives");
                Assert.False(File.Exists(Path.Combine(root, "Newtonsoft.Json.dll")), "everything unmapped is gone");
                Assert.False(File.Exists(Path.Combine(root, MarkerFileName)));
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    // ── The heal ────────────────────────────────────────────────────────────────

    /// <summary>An emptied-but-live shadow dir is healed back to its whole file set, in place —
    /// the four launch files the pre-#3559 heal copied cannot fill a hole this shape, and the
    /// dir may not be renamed aside (#2489: a live process's AppContext.BaseDirectory).</summary>
    [Fact]
    public void PublishShadowDir_HealsEveryFileTheManifestLists_WithoutRenamingTheDir()
    {
        var root = NewRoot("heal-whole-set");
        try
        {
            var shadowDir = Path.Combine(root, "key");
            WriteCompleteShadowDir(shadowDir);
            // What a prune under a live process leaves: the mapped entry assembly and Ncl.dll.
            File.Delete(Path.Combine(shadowDir, "Newtonsoft.Json.dll"));
            File.Delete(Path.Combine(shadowDir, "al-runner.deps.json"));
            File.Delete(Path.Combine(shadowDir, "runtimes", "win", "lib", "net8.0",
                "System.Diagnostics.EventLog.Messages.dll"));
            File.Delete(Path.Combine(shadowDir, MarkerFileName));
            Assert.False(NclShadowRuntime.IsShadowDirComplete(shadowDir, OrigFull));

            var tempDir = Path.Combine(root, "key.building.x");
            WriteCompleteShadowDir(tempDir);

            var published = NclShadowRuntime.PublishShadowDir(tempDir, shadowDir, OrigFull);

            Assert.Equal(shadowDir, published);
            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadowDir, OrigFull));
            Assert.True(File.Exists(Path.Combine(shadowDir, "runtimes", "win", "lib", "net8.0",
                "System.Diagnostics.EventLog.Messages.dll")));
            Assert.True(File.Exists(Path.Combine(shadowDir, "Newtonsoft.Json.dll")));
            Assert.DoesNotContain(Directory.GetDirectories(root),
                d => Path.GetFileName(d).Contains(".stale.", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
