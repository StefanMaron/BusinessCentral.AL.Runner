// DependencyPackageFingerprintTests — #4973: the pieces that decide when --server's affectedOnly may
// ignore a statement a packaged dependency executed. ServerAffectedSelectionPackagedDependencyTests
// drives them end to end.
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class DependencyPackageFingerprintTests
{
    private static readonly Guid Isv = Guid.Parse("6f0c7a64-5c1e-4b8e-9f43-1d8c3a2b7e11");
    private static readonly Guid Ms = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    private static readonly Func<Guid, string, bool> Loaded = (_, _) => true;

    // Reads the file every time: the production memo keys on (inode, size, mtime), which a test
    // rewriting a file within one timestamp tick could not rely on.
    private static string Sha(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
        : RunnerFingerprint.UnknownContentHash;

    private static AppManifest Manifest(Guid id, string publisher)
        => new(publisher, "App", new Version(1, 0, 0, 0), id, Array.Empty<DependencyRef>());

    [Fact]
    public void KeySegment_FollowsTheContent_OfNonMicrosoftPackagesOnly()
    {
        var dir = TestScratch.Dir("al-runner-dep-fingerprint");
        Directory.CreateDirectory(dir);
        var isvApp = Path.Combine(dir, "isv.app");
        var msApp = Path.Combine(dir, "ms.app");
        File.WriteAllBytes(isvApp, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(msApp, new byte[] { 9 });
        var resolved = new[] { (Manifest(Isv, "AL Runner"), isvApp), (Manifest(Ms, "Microsoft"), msApp) };

        var first = DependencyPackageFingerprint.KeySegment(resolved, _ => false, Loaded, Sha);
        Assert.Equal(new HashSet<Guid> { Isv }, DependencyPackageFingerprint.AppIdsIn("28.5|/tier|/pkgs" + first));

        File.WriteAllBytes(msApp, new byte[] { 8 });
        Assert.Equal(first, DependencyPackageFingerprint.KeySegment(resolved, _ => false, Loaded, Sha));

        File.WriteAllBytes(isvApp, new byte[] { 1, 2, 4 });
        Assert.NotEqual(first, DependencyPackageFingerprint.KeySegment(resolved, _ => false, Loaded, Sha));

        Assert.Equal("", DependencyPackageFingerprint.KeySegment(resolved, p => p == isvApp, Loaded, Sha));
    }

    // #4973 review: a package whose AppId runs a module loaded from elsewhere (#1892) is not what
    // executes, so it must not be vouched for.
    [Fact]
    public void KeySegment_LeavesOutAPackageThatIsNotTheLoadedModule()
    {
        var dir = TestScratch.Dir("al-runner-dep-fingerprint-provenance");
        Directory.CreateDirectory(dir);
        var isvApp = Path.Combine(dir, "isv.app");
        File.WriteAllBytes(isvApp, new byte[] { 1 });
        var resolved = new[] { (Manifest(Isv, "AL Runner"), isvApp) };

        Assert.Equal(new HashSet<Guid> { Isv }, DependencyPackageFingerprint.AppIdsIn(
            DependencyPackageFingerprint.KeySegment(resolved, _ => false, (id, p) => id == Isv && p == isvApp, Sha)));
        Assert.Empty(DependencyPackageFingerprint.AppIdsIn(
            DependencyPackageFingerprint.KeySegment(resolved, _ => false, (_, _) => false, Sha)));
    }

    [Fact]
    public void KeySegment_AnUnreadablePackage_NeverRepeatsAKey()
    {
        var missing = Path.Combine(TestScratch.Dir("al-runner-dep-fingerprint-missing"), "gone.app");
        var resolved = new[] { (Manifest(Isv, "AL Runner"), missing) };
        Assert.NotEqual(
            DependencyPackageFingerprint.KeySegment(resolved, _ => false, Loaded, Sha),
            DependencyPackageFingerprint.KeySegment(resolved, _ => false, Loaded, Sha));
    }

    [Fact]
    public void PackagedSourceRoots_KeepsOnlyCoveredApps_AndNeverARequestBundleOrItsParent()
    {
        var root = TestScratch.Dir("al-runner-dep-fingerprint-roots");
        string App(string name, Guid id)
        {
            var d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "app.json"), JsonSerializer.Serialize(new { id, name, publisher = "AL Runner", version = "1.0.0.0" }));
            return d;
        }
        var app = App("App", Isv);
        var other = App("Other", Guid.NewGuid());
        var testApp = App("App.Test", Guid.NewGuid());
        File.WriteAllText(Path.Combine(root, "app.json"), JsonSerializer.Serialize(new { id = Isv, name = "Parent", publisher = "AL Runner", version = "1.0.0.0" }));

        var roots = DependencyPackageFingerprint.PackagedSourceRoots(
            new[] { app, other, testApp, root }, new[] { testApp }, new HashSet<Guid> { Isv });

        Assert.Equal(new[] { Path.GetFullPath(app) }, roots);
        Assert.True(DependencyPackageFingerprint.IsUnderAny(Path.Combine(app, "src", "Helper.al"), roots));
        Assert.False(DependencyPackageFingerprint.IsUnderAny(Path.Combine(testApp, "Tests.al"), roots));
        Assert.False(DependencyPackageFingerprint.IsUnderAny(Path.Combine(root, "App2", "x.al"), roots));
        Assert.Empty(DependencyPackageFingerprint.PackagedSourceRoots(new[] { app }, new[] { testApp }, new HashSet<Guid>()));
    }

    // #4973 review: the request names a folder through a symlink and the registry holds its real
    // path (or the reverse); either way it is a request bundle and must never be ignored.
    [SkippableFact]
    public void PackagedSourceRoots_ExcludesARequestBundleNamedThroughASymlink()
    {
        var root = TestScratch.Dir("al-runner-dep-fingerprint-symlink");
        var real = Path.Combine(root, "App");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "app.json"), JsonSerializer.Serialize(new { id = Isv, name = "App", publisher = "AL Runner", version = "1.0.0.0" }));
        var link = Path.Combine(root, "AppLink");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows needs a privilege for symlinks; everywhere else this is a failure to measure.
            Skip.If(OperatingSystem.IsWindows(), $"symlinks need a privilege on Windows: {ex.Message}");
            Assert.Fail($"could not create a symlink here ({ex.GetType().Name}); this test measures nothing without one");
        }
        var ids = new HashSet<Guid> { Isv };

        Assert.Empty(DependencyPackageFingerprint.PackagedSourceRoots(new[] { real }, new[] { link }, ids));
        Assert.Empty(DependencyPackageFingerprint.PackagedSourceRoots(new[] { link }, new[] { real }, ids));
        Assert.Single(DependencyPackageFingerprint.PackagedSourceRoots(new[] { real }, new[] { Path.Combine(root, "Other") }, ids));
    }
}
