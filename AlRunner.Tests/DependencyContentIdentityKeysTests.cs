// #5081: the two places a --server process decides "this resolved package is the one I already
// compiled against" — the cross-request module-reuse fingerprint and the compile loader's warm
// dependency keys — must name the package's BYTES, not only its path and version: a library rebuilt
// at the same version and path holds different symbols. ServerCrossRequestDependencyRebuildTests
// drives both end to end; this class pins the key shapes and the unreadable-package answer.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyContentIdentityKeysTests : IDisposable
{
    private static readonly Guid LibId = Guid.Parse("5081c0de-0000-4a11-9111-00000000ff01");

    private readonly string _dir = TestScratch.Dir("al-runner-dep-content-identity-5081");

    public DependencyContentIdentityKeysTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppManifest Manifest()
        => new("repro", "Lib", new Version(1, 0, 0, 0), LibId, Array.Empty<DependencyRef>());

    private string Package(string name, byte[] bytes, int mtimeSeconds)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        // The file-identity memo keys on (inode, size, mtime): move the mtime so a rewrite of
        // the same length is never mistaken for the file it replaced.
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtimeSeconds));
        return path;
    }

    private static string Bundle(string root, params string?[] terms)
    {
        var al = Path.Combine(root, "A.Codeunit.al");
        File.WriteAllText(al, "codeunit 50000 A { }");
        var hashes = new Dictionary<string, string> { [Path.GetFullPath(al)] = "H" };
        return ProgramSupport.BundleSourceFingerprint(root, new[] { root }, hashes, terms) ?? "<null>";
    }

    [Fact]
    public void DependencyFingerprintTerm_SamePathAndVersion_FollowsTheBytes()
    {
        var path = Package("lib.app", new byte[] { 1, 2, 3 }, 1);
        var first = ProgramSupport.DependencyFingerprintTerm(Manifest(), path);

        path = Package("lib.app", new byte[] { 1, 2, 4 }, 2);
        var rebuilt = ProgramSupport.DependencyFingerprintTerm(Manifest(), path);
        Assert.NotNull(first);
        Assert.NotEqual(first, rebuilt);

        // Back to the first build: the term is the bytes' own, so it is the first term again.
        path = Package("lib.app", new byte[] { 1, 2, 3 }, 3);
        Assert.Equal(first, ProgramSupport.DependencyFingerprintTerm(Manifest(), path));
    }

    [Fact]
    public void DependencyFingerprintTerm_SameBytesRewritten_IsNotAChange()
    {
        var path = Package("lib.app", new byte[] { 7, 7, 7 }, 1);
        var first = ProgramSupport.DependencyFingerprintTerm(Manifest(), path);
        path = Package("lib.app", new byte[] { 7, 7, 7 }, 9);
        Assert.Equal(first, ProgramSupport.DependencyFingerprintTerm(Manifest(), path));
    }

    [Fact]
    public void DependencyFingerprintTerm_SameBytesAtAnotherPath_StaysADifferentTerm()
    {
        var a = Package("a.app", new byte[] { 5, 5 }, 1);
        var b = Package("b.app", new byte[] { 5, 5 }, 1);
        Assert.NotEqual(
            ProgramSupport.DependencyFingerprintTerm(Manifest(), a),
            ProgramSupport.DependencyFingerprintTerm(Manifest(), b));
    }

    [Fact]
    public void DependencyFingerprintTerm_ANonExistentPackage_IsNull()
        => Assert.Null(ProgramSupport.DependencyFingerprintTerm(Manifest(), Path.Combine(_dir, "missing.app")));

    [Fact]
    public void DependencyFingerprintTerm_AnUnidentifiablePackage_IsNullWhateverTheHashFunctionSays()
    {
        var path = Package("lib.app", new byte[] { 1 }, 1);
        Assert.Null(ProgramSupport.DependencyFingerprintTerm(Manifest(), path, _ => RunnerFingerprint.UnknownContentHash));
        Assert.Null(ProgramSupport.DependencyFingerprintTerm(Manifest(), path, _ => ""));
    }

    [Fact]
    public void BundleSourceFingerprint_FollowsTheDependencyTerm_AndRefusesAMissingOne()
    {
        var one = Bundle(_dir, "dep:a");
        Assert.Equal(one, Bundle(_dir, "dep:a"));
        Assert.NotEqual(one, Bundle(_dir, "dep:b"));
        // A package that could not be identified must never produce a fingerprint another
        // directory's could equal.
        Assert.Equal("<null>", Bundle(_dir, "dep:a", null));
        Assert.Equal("<null>", Bundle(_dir, (string?)null));
    }

    [Fact]
    public void PackageServedDepKeys_SamePathAndVersion_FollowTheBytes()
    {
        var path = Package("lib.app", new byte[] { 1, 2, 3 }, 1);
        var deps = new[] { (Manifest(), path) };
        var inventory = new List<BcCompiler.PackageScanEntry>
            { new(Path.GetFullPath(path), LibId, "repro", "Lib", new Version(1, 0, 0, 0)) };
        var first = BcCompiler.PackageServedDepKeys(deps, inventory);

        Assert.Equal(first, BcCompiler.PackageServedDepKeys(deps, inventory));

        Package("lib.app", new byte[] { 1, 2, 4 }, 2);
        var rebuilt = BcCompiler.PackageServedDepKeys(deps, inventory);
        // The loader is reused when the current keys are a subset of the ones it was built for.
        Assert.False(rebuilt.IsSubsetOf(first));

        Package("lib.app", new byte[] { 1, 2, 3 }, 3);
        Assert.Equal(first, BcCompiler.PackageServedDepKeys(deps, inventory));
    }

    [Fact]
    public void PackageServedDepKeys_AnUnreadablePackage_NeverRepeatsAKey()
    {
        var path = Path.Combine(_dir, "gone.app");
        var deps = new[] { (Manifest(), path) };
        var inventory = new List<BcCompiler.PackageScanEntry>
            { new(Path.GetFullPath(path), LibId, "repro", "Lib", new Version(1, 0, 0, 0)) };
        var first = BcCompiler.PackageServedDepKeys(deps, inventory);
        var second = BcCompiler.PackageServedDepKeys(deps, inventory);
        Assert.Single(first);
        Assert.False(second.IsSubsetOf(first));
    }
}
