// SafeDirectoryScanSymlinkLoopTests — RED->GREEN guard for issue #2219.
//
// A directory symlink back to an ancestor made SafeDirectoryScan re-walk the same tree under
// ever-longer spellings until PATH_MAX stopped it: one real `.alpackages` came back as 41
// hits, and the ENAMETOOLONG at the bottom was reported as an unreadable directory. The walk
// now refuses to descend into a directory whose real path is already on the current descent
// chain. The negative tests pin that a symlink to a directory NOT on the chain is still
// followed, so "skip every symlink" cannot pass here.
using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class SafeDirectoryScanSymlinkLoopTests : IDisposable
{
    private const string NoSymlinks = "the filesystem does not allow creating directory symlinks here";

    private readonly string _root;

    public SafeDirectoryScanSymlinkLoopTests()
    {
        _root = TestScratch.Dir("al-runner-scan-symlink-loop");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [SkippableFact]
    public void LinkBackToTheRoot_FindsTheOneRealAlpackages_Once()
    {
        var real = Path.Combine(_root, "a", "b", ".alpackages");
        Directory.CreateDirectory(real);
        Skip.IfNot(TryCreateSymlink(_root, Path.Combine(_root, "a", "b", "back")), NoSymlinks);

        var hits = SafeDirectoryScan.Directories(_root, ".alpackages", out var inaccessible);

        Assert.Equal(new[] { real }, hits);
        // The loop used to end in ENAMETOOLONG, which surfaced as an "unreadable" directory.
        Assert.Empty(inaccessible);
    }

    [SkippableFact]
    public void LinkBackToTheRoot_ListsEachRealFileOnce()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllText(Path.Combine(_root, "Root.al"), "x");
        File.WriteAllText(Path.Combine(_root, "a", "b", "Leaf.al"), "x");
        Skip.IfNot(TryCreateSymlink(_root, Path.Combine(_root, "a", "b", "back")), NoSymlinks);

        var hits = SafeDirectoryScan.Files(_root, "*.al", out var inaccessible);

        Assert.Equal(
            new[] { Path.Combine("Root.al"), Path.Combine("a", "b", "Leaf.al") }
                .OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            hits.Select(p => Path.GetRelativePath(_root, p)).ToArray());
        Assert.Empty(inaccessible);
    }

    [SkippableFact]
    public void MutualLinksBetweenSiblings_Terminate_WithoutReportingUnreadableDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "left", ".alpackages"));
        Directory.CreateDirectory(Path.Combine(_root, "right"));
        Skip.IfNot(TryCreateSymlink(Path.Combine(_root, "right"), Path.Combine(_root, "left", "to-right")), NoSymlinks);
        Skip.IfNot(TryCreateSymlink(Path.Combine(_root, "left"), Path.Combine(_root, "right", "to-left")), NoSymlinks);

        var hits = SafeDirectoryScan.Directories(_root, ".alpackages", out var inaccessible);

        // Two spellings of one directory, each reached by a chain that is not itself a cycle:
        // left/.alpackages directly, and right/to-left/.alpackages. Not 40-odd.
        Assert.Equal(
            new[]
            {
                Path.Combine("left", ".alpackages"),
                Path.Combine("right", "to-left", ".alpackages"),
            },
            hits.Select(p => Path.GetRelativePath(_root, p)).ToArray());
        Assert.Empty(inaccessible);
    }

    // ── negative direction: a symlink that is not a cycle must still be followed ────────

    [SkippableFact]
    public void LinkToADirectoryOutsideTheWalk_IsStillFollowed()
    {
        var outside = TestScratch.Dir("al-runner-scan-symlink-target");
        Directory.CreateDirectory(Path.Combine(outside, "vendored", ".alpackages"));
        File.WriteAllText(Path.Combine(outside, "vendored", "Shared.al"), "x");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "app"));
            Skip.IfNot(TryCreateSymlink(outside, Path.Combine(_root, "app", "linked")), NoSymlinks);

            Assert.Equal(
                new[] { Path.Combine("app", "linked", "vendored", ".alpackages") },
                SafeDirectoryScan.Directories(_root, ".alpackages")
                    .Select(p => Path.GetRelativePath(_root, p)).ToArray());
            Assert.Equal(
                new[] { Path.Combine("app", "linked", "vendored", "Shared.al") },
                SafeDirectoryScan.Files(_root, "*.al")
                    .Select(p => Path.GetRelativePath(_root, p)).ToArray());
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void LinkToASiblingDirectory_IsFollowed_BecauseASiblingIsNotAnAncestor()
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared", ".alpackages"));
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        Skip.IfNot(TryCreateSymlink(Path.Combine(_root, "shared"), Path.Combine(_root, "app", "shared-link")), NoSymlinks);

        Assert.Equal(
            new[]
            {
                Path.Combine("app", "shared-link", ".alpackages"),
                Path.Combine("shared", ".alpackages"),
            },
            SafeDirectoryScan.Directories(_root, ".alpackages")
                .Select(p => Path.GetRelativePath(_root, p)).ToArray());
    }

    private static bool TryCreateSymlink(string target, string link)
    {
        try { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }
}
