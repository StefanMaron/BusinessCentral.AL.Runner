// RED baseline for issue #2206 — temporary, compiles against the CURRENT API only.
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
        foreach (var d in Directory.GetDirectories(_dir, "*", SearchOption.TopDirectoryOnly))
            Restore(d);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void Restore(string d)
    {
        try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        string[] subs; try { subs = Directory.GetDirectories(d); } catch { return; }
        foreach (var s in subs) Restore(s);
    }

    private static bool TryMakeUnreadable(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.None); } catch { return false; }
        try { Directory.GetDirectories(path); return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

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

        Assert.Single(found);
        Assert.Equal(pkg, found[0]);
    }

    [SkippableFact]
    public void UnreadableSubdirectory_DoesNotAbortTheRestOfTheWalk()
    {
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

    [Fact]
    public void HiddenParentDirectory_IsStillTraversed()
    {
        var bundle = Path.Combine(_dir, "hidden-parent");
        var pkg = Path.Combine(bundle, ".worktrees", "wt1", "app", ".alpackages");
        Directory.CreateDirectory(pkg);

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        Assert.Single(found);
        Assert.Equal(pkg, found[0]);
    }
}
