// DependencyCacheKeyContentHashMemoTests — issue #3043.
//
// `DependencyLoader.ComputeSourceDependencyCacheKey` names `compiled-deps/<key>.dll` and its
// five metadata sidecars, so the string it returns is PERSISTED and read by later runs and by
// other processes. It used to end its key with
//
//     using (var fs = File.OpenRead(appPath))
//         WriteLine($"app-bytes:{Convert.ToHexString(sha.ComputeHash(fs))}");
//
// which is a second full SHA-256 pass over bytes `RunnerFingerprint.ComputeFileContentHashMemoized`
// has already answered for — `AppLoader.ReadManifest` (#2987), `AppLoader.ExtractAllDllPaths`,
// `BcAppSymbolCache` and `ProgramSupport.DependencyContentTerm` all go through that one memo, and
// since #2987 every scanned package is hashed by the manifest index, so "already hashed" is the
// normal case rather than the exception.
//
// Measured on this repo's own `tests/runner-extras` leg (both package caches, cold `--cache`
// root and again warm, probe counting every SHA-256 file read): 7 packages, 634,797 bytes,
// re-read identically on both runs, and all 7 were already in the memo (750 memo lookups
// apiece, one computation). Small — that is the honest number — but free.
//
// The reason #3042 filed this instead of folding it in is that both hazards land on a
// persisted key, and NEITHER is visible in the returned value:
//
//   CASING — `Convert.ToHexString` is uppercase, `ComputeContentHash` lowercases. Substituting
//   the memo's answer directly changes the key's TEXT for identical bytes, orphaning every
//   `compiled-deps` entry on every machine and CI cache. `Key_IsUnchangedByTheCasingOfTheHashItIsGiven`
//   and `Key_IsByteIdenticalToTheFreshReadKeyThisReplaces` are the two that fail if the
//   `.ToUpperInvariant()` is dropped.
//
//   SENTINEL — `File.OpenRead` threw for an unreadable package; the memo answers
//   `UnknownContentHash`. Passed through, every unidentifiable package gets ONE key, so the
//   first one's compiled DLL is served for all of them. `UnknownContentHash_Throws_*` pins the
//   refusal, and pins that two different unidentifiable packages cannot collapse onto one key.
//
// The memo-count test is the one that was RED: the old code did not consult the memo at all, so
// `ContentHashComputationCountForTests` did not move when the key was computed. Asserting only
// that the count "does not increase" — the shape the issue suggested — passes on the OLD code
// too and proves nothing; the assertion with teeth is that computing the key on a cold memo
// increments it by exactly one, i.e. the read now goes THROUGH the memo where every other layer
// can share it.

using AlRunner;
using AlRunner.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Reads the process-global content-hash computation counter as an exact delta and clears the
// shared memo — the same reason RunnerFingerprintFileIdentityMemoTests joins this collection.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyCacheKeyContentHashMemoTests : IDisposable
{
    private readonly string _root;

    public DependencyCacheKeyContentHashMemoTests()
    {
        _root = TestScratch.Dir("al-runner-depkey-memo");
        Directory.CreateDirectory(_root);
        RunnerFingerprint.ClearFileContentHashMemoForTests();
    }

    public void Dispose()
    {
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static AppManifest Manifest(string name = "Dep One") => new(
        Publisher: "Contoso",
        Name: name,
        Version: new Version(1, 2, 3, 4),
        AppId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Dependencies: new[]
        {
            new DependencyRef(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "Base", "Microsoft", new Version(28, 0, 0, 0)),
        });

    private string WritePackage(string name, string body)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes(body));
        return p;
    }

    /// <summary>
    /// The key exactly as the pre-#3043 code computed it: a fresh <see cref="File.OpenRead"/>
    /// and <see cref="Convert.ToHexString(byte[])"/> — uppercase — for the <c>app-bytes:</c>
    /// line, everything else in the same order. Reproduced here rather than hardcoded because
    /// the key also carries the runner's own content hash and the selected BC version, both of
    /// which change per build; what has to stay fixed is that the two agree.
    /// </summary>
    private static string FreshReadReferenceKey(AppManifest manifest, string appPath)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        void WriteLine(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\n");
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteLine("schema:v2");
        RunnerFingerprint.WriteKeyLines(WriteLine);
        WriteLine($"app:{manifest.AppId}:{manifest.Publisher}:{manifest.Name}:{manifest.Version}");
        foreach (var dep in manifest.Dependencies.OrderBy(
                     d => $"{d.Publisher}/{d.Name}/{d.Version}/{d.AppId}", StringComparer.OrdinalIgnoreCase))
            WriteLine($"dep:{dep.AppId}:{dep.Publisher}:{dep.Name}:{dep.Version}");
        using (var fs = File.OpenRead(appPath))
            WriteLine($"app-bytes:{Convert.ToHexString(sha.ComputeHash(fs))}");

        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
    }

    // ---------------------------------------------------------------------------------------
    // The saving. RED before #3043: the first assertion read 0, because the key was computed
    // from a File.OpenRead the memo never saw.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Key_HashesThroughTheSharedMemo_AndASecondKeyForTheSamePackageReadsNothing()
    {
        var pkg = WritePackage("dep.app", "package bytes v1");
        var m = Manifest();

        var before = RunnerFingerprint.ContentHashComputationCountForTests;
        var key1 = DependencyLoader.ComputeSourceDependencyCacheKeyCore(
            m, pkg, p => RunnerFingerprint.ComputeFileContentHashMemoized(p));

        // Went THROUGH the memo — this is the half that was RED. A key computed from its own
        // File.OpenRead leaves this counter untouched, so "did not increase" is not the claim.
        Assert.Equal(before + 1, RunnerFingerprint.ContentHashComputationCountForTests);

        var key2 = DependencyLoader.ComputeSourceDependencyCacheKeyCore(
            m, pkg, p => RunnerFingerprint.ComputeFileContentHashMemoized(p));

        // ...and the second key cost no read at all.
        Assert.Equal(before + 1, RunnerFingerprint.ContentHashComputationCountForTests);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Key_ForAPackageAnotherLayerAlreadyHashed_CostsNoRead()
    {
        // The real shape: AppLoader.ReadManifest hashed this package while indexing it, long
        // before DependencyLoader reaches Tier 3 for it.
        var pkg = WritePackage("dep.app", "package bytes v1");
        var primed = RunnerFingerprint.ComputeFileContentHashMemoized(pkg);
        Assert.Equal(64, primed.Length);

        var before = RunnerFingerprint.ContentHashComputationCountForTests;
        var key = DependencyLoader.ComputeSourceDependencyCacheKeyCore(
            Manifest(), pkg, p => RunnerFingerprint.ComputeFileContentHashMemoized(p));

        Assert.Equal(before, RunnerFingerprint.ContentHashComputationCountForTests);
        Assert.Equal(64, key.Length);
    }

    [Fact]
    public void Key_NeverOpensThePackageItself_OnlyTheHashDelegateDoes()
    {
        // Decisive and portable: the package does not exist, so anything that opened it would
        // throw. The delegate is the ONLY route to the bytes.
        var absent = Path.Combine(_root, "not-on-disk.app");
        Assert.False(File.Exists(absent));

        var calls = new List<string>();
        var key = DependencyLoader.ComputeSourceDependencyCacheKeyCore(
            Manifest(), absent,
            p => { calls.Add(p); return new string('a', 64); });

        Assert.Equal(new[] { absent }, calls);
        Assert.Equal(64, key.Length);
    }

    // ---------------------------------------------------------------------------------------
    // Hazard 1: the persisted key's value must not move.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Key_IsByteIdenticalToTheFreshReadKeyThisReplaces()
    {
        var pkg = WritePackage("dep.app", "package bytes v1");
        var m = Manifest();

        Assert.Equal(
            FreshReadReferenceKey(m, pkg),
            DependencyLoader.ComputeSourceDependencyCacheKeyCore(
                m, pkg, p => RunnerFingerprint.ComputeFileContentHashMemoized(p)));
    }

    [Fact]
    public void Key_IsUnchangedByTheCasingOfTheHashItIsGiven()
    {
        // The memo lowercases; Convert.ToHexString uppercases. The key must not be able to tell
        // the difference, or swapping one source of the hash for the other silently orphans the
        // on-disk cache. Drop the `.ToUpperInvariant()` in AppBytesTerm and these two keys stop
        // being equal.
        var pkg = WritePackage("dep.app", "package bytes v1");
        var m = Manifest();
        const string Lower = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var upper = Lower.ToUpperInvariant();

        var fromLower = DependencyLoader.ComputeSourceDependencyCacheKeyCore(m, pkg, _ => Lower);
        var fromUpper = DependencyLoader.ComputeSourceDependencyCacheKeyCore(m, pkg, _ => upper);

        Assert.Equal(fromUpper, fromLower);
    }

    [Fact]
    public void Key_StillChangesWhenThePackageBytesChange()
    {
        // The negative direction of the two pins above: agreeing on casing must not have made
        // the app-bytes line inert. Two different packages, two different keys.
        var m = Manifest();
        var a = WritePackage("a.app", "package bytes v1");
        var b = WritePackage("b.app", "package bytes v2 — different");

        var keyA = DependencyLoader.ComputeSourceDependencyCacheKeyCore(
            m, a, p => RunnerFingerprint.ComputeFileContentHashMemoized(p));
        var keyB = DependencyLoader.ComputeSourceDependencyCacheKeyCore(
            m, b, p => RunnerFingerprint.ComputeFileContentHashMemoized(p));

        Assert.NotEqual(keyA, keyB);
    }

    // ---------------------------------------------------------------------------------------
    // Hazard 2: the `unknown` sentinel must never become a shared key.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void UnknownContentHash_Throws_RatherThanGivingEveryUnidentifiablePackageOneKey()
    {
        var m = Manifest();
        var a = Path.Combine(_root, "unreadable-a.app");
        var b = Path.Combine(_root, "unreadable-b.app");

        var exA = Assert.Throws<FileNotFoundException>(() =>
            DependencyLoader.ComputeSourceDependencyCacheKeyCore(
                m, a, _ => RunnerFingerprint.UnknownContentHash));
        var exB = Assert.Throws<FileNotFoundException>(() =>
            DependencyLoader.ComputeSourceDependencyCacheKeyCore(
                m, b, _ => RunnerFingerprint.UnknownContentHash));

        // Each names its OWN package, so nothing aliases the two onto one diagnosis either.
        Assert.Contains("unreadable-a.app", exA.Message);
        Assert.Contains("unreadable-b.app", exB.Message);
        Assert.DoesNotContain("unreadable-b.app", exA.Message);
        Assert.Equal(a, exA.FileName);
        Assert.Equal(b, exB.FileName);
    }

    [Fact]
    public void EmptyContentHash_Throws_ForTheSameReason()
    {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            DependencyLoader.ComputeSourceDependencyCacheKeyCore(
                Manifest(), Path.Combine(_root, "empty-hash.app"), _ => ""));
        Assert.Contains("empty-hash.app", ex.Message);
    }

    [Fact]
    public void MissingPackage_StillThrows_AsTheFreshReadDid()
    {
        // End-to-end through the production overload (the real memo, no injected delegate):
        // File.OpenRead threw FileNotFoundException for a package that is not there, and the
        // memo's `unknown` answer must not have quietly turned that into a cache key.
        var absent = Path.Combine(_root, "gone.app");
        Assert.Throws<FileNotFoundException>(() =>
            DependencyLoader.ComputeSourceDependencyCacheKeyCore(
                Manifest(), absent, p => RunnerFingerprint.ComputeFileContentHashMemoized(p)));
    }
}
