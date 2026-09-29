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

        var first = DependencyPackageFingerprint.KeySegment(resolved, _ => false);
        Assert.Equal(new HashSet<Guid> { Isv }, DependencyPackageFingerprint.AppIdsIn("28.5|/tier|/pkgs" + first));

        File.WriteAllBytes(msApp, new byte[] { 8 });
        Assert.Equal(first, DependencyPackageFingerprint.KeySegment(resolved, _ => false));

        File.WriteAllBytes(isvApp, new byte[] { 1, 2, 4 });
        Assert.NotEqual(first, DependencyPackageFingerprint.KeySegment(resolved, _ => false));

        Assert.Equal("", DependencyPackageFingerprint.KeySegment(resolved, p => p == isvApp));
    }

    [Fact]
    public void KeySegment_AnUnreadablePackage_NeverRepeatsAKey()
    {
        var missing = Path.Combine(TestScratch.Dir("al-runner-dep-fingerprint-missing"), "gone.app");
        var resolved = new[] { (Manifest(Isv, "AL Runner"), missing) };
        Assert.NotEqual(
            DependencyPackageFingerprint.KeySegment(resolved, _ => false),
            DependencyPackageFingerprint.KeySegment(resolved, _ => false));
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
}
