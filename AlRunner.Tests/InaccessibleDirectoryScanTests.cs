// InaccessibleDirectoryScanTests — RED->GREEN guard for issue #2206.
//
// The bug: pointing the runner at any directory tree containing an unreadable
// subdirectory killed the process with an unhandled UnauthorizedAccessException and
// exit 134 (SIGABRT). `al-runner .` from a repo root is the first thing a newcomer
// types, and unreadable subdirectories under a common root (/tmp/systemd-private-*,
// another user's home, a root-owned build cache) are ordinary on Linux and macOS.
//
// The root cause was a `try` guarding the CONSTRUCTION of a lazy enumerator rather than
// its ITERATION:
//
//     try { found = Directory.EnumerateDirectories(b, ".alpackages", AllDirectories); }
//     catch { continue; }
//     foreach (var dir in found)   // <- the throw happens HERE, outside the guard
//
// Directory.EnumerateDirectories does no I/O until MoveNext, so the catch could never
// fire. The same shape appeared at four more call sites; all now route through
// SafeDirectoryScan, whose walk keeps every filesystem call inside a per-directory
// guard.
//
// Two directions are pinned here, because a fix that only stops the crash is not
// enough:
//   * an unreadable subdirectory must be SKIPPED, and the readable .alpackages beside
//     and BELOW it must still be found (a fix that aborts the whole bundle on the first
//     denial would pass a "did not throw" test while silently losing packages);
//   * the skipped path must be REPORTED, so the caller can name it. Silently skipping
//     turns a permissions problem into a mysterious missing-dependency error later,
//     which is the exact failure mode issue #2213 exists to remove.
//
// And one trap is pinned that the obvious one-line fix walks straight into: switching to
// `new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }`
// looks like the tidy modern spelling, but EnumerationOptions defaults AttributesToSkip
// to Hidden|System, and on Unix every dot-directory is Hidden. That spelling finds
// ZERO .alpackages on Linux and macOS — measured on this repo: 0 hits versus 94 for the
// SearchOption overload. HiddenParentDirectory_IsStillTraversed is the regression guard.

using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class InaccessibleDirectoryScanTests : IDisposable
{
    private readonly string _dir;

    public InaccessibleDirectoryScanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "al-runner-inaccessible", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Restore permissions first or the recursive delete fails on the locked dirs.
        foreach (var d in SafeDirectoryScan.Directories(_dir, "*", out _))
        {
            try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// Makes <paramref name="path"/> unreadable and returns true only if that actually
    /// bites — running as root, or on a filesystem that ignores mode bits, permission
    /// checks are bypassed and the test has nothing to prove. This is a CAPABILITY probe
    /// rather than a uid check on purpose: it is the property the test depends on,
    /// and it is also correct on Windows and on a root-squashed mount.
    /// </summary>
    private static bool TryMakeUnreadable(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.None); }
        catch { return false; }
        try
        {
            // If enumeration still succeeds, the mode bits did not bite (root).
            Directory.GetDirectories(path);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    // ── The reported crash ────────────────────────────────────────────────────

    [SkippableFact]
    public void UnreadableSubdirectory_IsSkippedAndTheReadableAlpackagesIsStillFound()
    {
        var bundle = Path.Combine(_dir, "repo");
        var pkg = Path.Combine(bundle, "myapp", ".alpackages");
        Directory.CreateDirectory(pkg);
        var locked = Path.Combine(bundle, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        Skip.IfNot(TryMakeUnreadable(locked), "permission bits do not bite here (running as root?)");

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        // Not merely "did not throw": the readable package dir must still come back.
        Assert.Single(found);
        Assert.Equal(pkg, found[0]);
    }

    [SkippableFact]
    public void UnreadableSubdirectory_DoesNotAbortTheRestOfTheWalk()
    {
        // The cheap wrong fix — move `continue` so the first denial abandons the whole
        // bundle — stops the crash and silently loses every package below the denial.
        // Order the walk cannot control decides which of these it meets first, so both
        // siblings must survive regardless.
        var bundle = Path.Combine(_dir, "repo2");
        var before = Path.Combine(bundle, "aaa-app", ".alpackages");
        var after = Path.Combine(bundle, "zzz-app", ".alpackages");
        Directory.CreateDirectory(before);
        Directory.CreateDirectory(after);
        var locked = Path.Combine(bundle, "mmm-locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        Skip.IfNot(TryMakeUnreadable(locked), "permission bits do not bite here (running as root?)");

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        Assert.Equal(2, found.Count);
        Assert.Contains(before, found);
        Assert.Contains(after, found);
    }

    // ── The skipped path is reported, not swallowed ───────────────────────────

    [SkippableFact]
    public void UnreadableSubdirectory_IsReportedByItsFullPath()
    {
        var bundle = Path.Combine(_dir, "repo3");
        Directory.CreateDirectory(Path.Combine(bundle, "app", ".alpackages"));
        var locked = Path.Combine(bundle, "secret");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        Skip.IfNot(TryMakeUnreadable(locked), "permission bits do not bite here (running as root?)");

        ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle }, out var inaccessible);

        Assert.Single(inaccessible);
        Assert.Equal(locked, inaccessible[0]);
    }

    [Fact]
    public void FullyReadableTree_ReportsNothingInaccessibleAndFindsEverything()
    {
        // The negative direction: a fix that reports every directory as inaccessible, or
        // that reports a constant non-empty list, fails here.
        var bundle = Path.Combine(_dir, "clean");
        var p1 = Path.Combine(bundle, "app1", ".alpackages");
        var p2 = Path.Combine(bundle, "nested", "deep", "app2", ".alpackages");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle }, out var inaccessible);

        Assert.Empty(inaccessible);
        Assert.Equal(2, found.Count);
        Assert.Contains(p1, found);
        Assert.Contains(p2, found);
    }

    // ── The EnumerationOptions trap ───────────────────────────────────────────

    [Fact]
    public void HiddenParentDirectory_IsStillTraversed()
    {
        // Regression guard against "simplifying" the walk to EnumerationOptions defaults.
        // AttributesToSkip defaults to Hidden|System, and on Unix EVERY dot-directory is
        // Hidden — so that spelling would skip `.claude`, and skip `.alpackages` itself.
        // Real layouts put packages under hidden parents (worktrees, tool caches), and
        // `.alpackages` is a dot-directory by definition, so both must be traversable.
        var bundle = Path.Combine(_dir, "hidden-parent");
        var pkg = Path.Combine(bundle, ".worktrees", "wt1", "app", ".alpackages");
        Directory.CreateDirectory(pkg);

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        Assert.Single(found);
        Assert.Equal(pkg, found[0]);
    }

    // ── The warning the user actually reads ───────────────────────────────────

    [Fact]
    public void InaccessibleWarning_EmptyList_IsNull()
    {
        // Measured on real trees: an AL repo root (94,740 dirs) and a workspace root
        // (307,833 dirs) both hit ZERO inaccessible directories. The warning must
        // therefore be silent on the trees people actually point the runner at — this is
        // what makes warning affordable rather than noise.
        Assert.Null(ProvisioningCheck.FormatInaccessibleScanWarning(Array.Empty<string>()));
    }

    [Fact]
    public void InaccessibleWarning_NamesEveryPathAndSaysWhyItMatters()
    {
        var warning = ProvisioningCheck.FormatInaccessibleScanWarning(
            new[] { "/tmp/systemd-private-abc", "/tmp/systemd-private-def" });

        Assert.NotNull(warning);
        Assert.Contains("/tmp/systemd-private-abc", warning);
        Assert.Contains("/tmp/systemd-private-def", warning);
        // It must say what was being looked for, or the reader cannot tell whether the
        // skip could have mattered.
        Assert.Contains(".alpackages", warning);
    }

    [Fact]
    public void InaccessibleWarning_LongList_IsCappedButSaysHowManyMore()
    {
        // Bounding the output matters: a tree with hundreds of unreadable directories
        // must not bury the run's real output. Truncation without a count would hide the
        // scale, so the remainder is stated.
        var many = Enumerable.Range(0, 25).Select(i => $"/x/locked{i}").ToArray();

        var warning = ProvisioningCheck.FormatInaccessibleScanWarning(many, maxListed: 5);

        Assert.NotNull(warning);
        Assert.Contains("/x/locked0", warning);
        Assert.Contains("/x/locked4", warning);
        Assert.DoesNotContain("/x/locked5", warning);
        Assert.Contains("20 more", warning);
    }

    // ── The shared helper's own contract ──────────────────────────────────────

    [SkippableFact]
    public void SafeDirectoryScan_Files_UnreadableSubdirectory_SkippedAndReported()
    {
        // The same defect existed for the *.app file scans, which is a different overload
        // on the same helper — pinned so the file side cannot regress independently.
        var root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(Path.Combine(root, "ok"));
        File.WriteAllText(Path.Combine(root, "ok", "a.app"), "x");
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        File.WriteAllText(Path.Combine(locked, "hidden.app"), "x");
        Skip.IfNot(TryMakeUnreadable(locked), "permission bits do not bite here (running as root?)");

        var files = SafeDirectoryScan.Files(root, "*.app", out var inaccessible);

        Assert.Single(files);
        Assert.Equal(Path.Combine(root, "ok", "a.app"), files[0]);
        Assert.Contains(locked, inaccessible);
    }

    [Fact]
    public void SafeDirectoryScan_NonexistentRoot_ReturnsEmptyAndDoesNotReportInaccessible()
    {
        // A missing path is not a permissions problem and must not be reported as one —
        // otherwise the warning would fire on a plain typo and mislead the reader.
        var gone = Path.Combine(_dir, "not-here");

        var dirs = SafeDirectoryScan.Directories(gone, "*", out var inaccessible);

        Assert.Empty(dirs);
        Assert.Empty(inaccessible);
    }
}
