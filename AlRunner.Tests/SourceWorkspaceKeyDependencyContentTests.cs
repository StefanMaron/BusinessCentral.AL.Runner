// SourceWorkspaceKeyDependencyContentTests — issue #2846 case 1: the layered pre-pass's
// workspace directory key must identify the RESOLVED dependency packages by their content.
//
// The defect
// ----------
// `ProgramSupport.ComputeSourceWorkspaceKey` (AlRunner/ProgramSupport/SiblingCompile.cs) names
// the directory `workspace-deps/<key[..12]>` that `RunLayeredPrePass` and `RunSourceDepPrePass`
// write two artifacts into:
//
//   <Pub>_<Name>_<Ver>.app            — emitted by InProcessAppPackager from the SOURCE dir
//   <Pub>_<Name>_<Ver>.symbols.json   — the output of COMPILING that app's public surface
//   <Pub>_<Name>_<Ver>.symbols.deps.json    against the resolved package closure
//
// The key carried the source app's identity, its DECLARED dependencies (id/publisher/name/
// version) and a SHA-256 per `.al`/`app.json` file. The `.app` is entirely source-derived, so
// those terms cover it. The `*.symbols.json` is not: it is the compiler's view of the app's
// public surface *as compiled against the resolved dependency packages' bytes*, and the bytes
// of the winning packages appeared nowhere in the key.
//
// So swapping a third-party dependency `.app` for different bytes at the same declared version
// produced a workspace HIT, and the dependent bundle compiled against the PREVIOUS public
// surface. Nothing fails: same exit code, same green tests, code linked against a dependency
// surface the run never saw. Identical shape to #2754 one cache layer over — see
// CacheKeyDependencyContentIdentityTests for that one, and commit f3ca2b00 for the fix this
// follows.
//
// Why the #2754 fix does not already cover it: the AL-output key's dep term for a layered
// sibling hashes the workspace `.app`, whose content is source-derived. A stale `symbols.json`
// sitting beside it is invisible to that hash.
//
// The arms
// --------
// NEGATIVE (the collision) — the same declared dependency resolved to packages with the same
// byte LENGTH and the same MTIME but different BYTES must not share a workspace key.
//
// POSITIVE (the cache must still hit) — byte-identical packages at a different path with a
// different mtime must produce the SAME key. Without this arm the collision test above is
// satisfied by keying on something always-unique, which would pass while destroying every
// workspace cache hit and re-running the ~22s-per-impl symbol compile on every invocation.
//
// Plus the two properties the change must not break: the key still moves when a source file
// changes, and it still distinguishes two different resolved closures for identical source.
//
// A note on the fixture, because it is easy to get wrong: BcAppSymbolCache.ComputeAppContentHash
// memoizes per FULL PATH for the life of the process (deliberately — see its comment). A test
// that rewrote one package in place at a single path would therefore be served the first hash
// and read as GREEN no matter what the key does. Every arm here writes its packages at
// DISTINCT paths for that reason.

using Xunit;
using AlRunner;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class SourceWorkspaceKeyDependencyContentTests : IDisposable
{
    private static readonly Guid SourceAppId = new("2846a001-1111-4222-8333-444455556666");
    private static readonly Guid DepAppId = new("2846b002-1111-4222-8333-444455556666");
    private const string DepName = "Fabrikam Layered Dep";
    private const string DepPublisher = "Fabrikam ISV";
    private static readonly Version DepVersion = new(1, 0, 0, 0);

    private readonly string _scratch;

    public SourceWorkspaceKeyDependencyContentTests()
    {
        _scratch = TestScratch.Dir("al-runner-workspace-key-depcontent");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The defect. Two resolved packages with the same declared id/publisher/name/version, the
    /// same byte length and the same mtime, differing only in their bytes, must produce
    /// different workspace keys — otherwise the second run reuses the first run's
    /// <c>*.symbols.json</c>, which was compiled against the other package's public surface.
    /// </summary>
    [SkippableFact]
    public void SameDeclaredDependencyDifferentPackageBytes_ProducesDifferentWorkspaceKey()
    {
        TestArtifacts.SkipIfMissing();

        var (dir, ids) = WriteSourceApp("src-collide");

        var pkgA = WritePackage("pkg-a", fill: (byte)'A');
        var pkgB = WritePackage("pkg-b", fill: (byte)'B');
        StampSameMtime(pkgA, pkgB);

        // Preconditions, asserted rather than assumed: without all three this test would not
        // be exercising the defect at all.
        Assert.Equal(new FileInfo(pkgA).Length, new FileInfo(pkgB).Length);
        Assert.Equal(new FileInfo(pkgA).LastWriteTimeUtc.Ticks, new FileInfo(pkgB).LastWriteTimeUtc.Ticks);
        Assert.False(File.ReadAllBytes(pkgA).AsSpan().SequenceEqual(File.ReadAllBytes(pkgB)),
            "the two synthetic packages are byte-identical — the fixture is not exercising the defect");

        var keyA = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkgA), resolutionFailure: null);
        var keyB = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkgB), resolutionFailure: null);

        // Concrete shape, not just "different": a 64-char lowercase hex digest. A key that
        // degraded to an empty string or a constant would satisfy an inequality-only test in
        // one direction and this one in neither.
        AssertIsWorkspaceKey(keyA);
        AssertIsWorkspaceKey(keyB);
        Assert.NotEqual(keyA, keyB);
    }

    /// <summary>
    /// The direction that keeps the fix honest: byte-identical resolved packages at a different
    /// path with a different mtime must key IDENTICALLY. A key that simply varied per resolved
    /// file path (or per stat) would satisfy the collision arm while destroying the workspace
    /// cache — and re-running the per-impl symbol compile the workspace dir exists to avoid.
    /// </summary>
    [SkippableFact]
    public void ByteIdenticalPackagesAtDifferentPathsAndMtimes_ProduceTheSameWorkspaceKey()
    {
        TestArtifacts.SkipIfMissing();

        var (dir, ids) = WriteSourceApp("src-stable");

        var pkgA = WritePackage("same-a", fill: (byte)'A');
        var pkgB = WritePackage("same-b", fill: (byte)'A');
        File.SetLastWriteTimeUtc(pkgA, new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(pkgB, new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        Assert.True(File.ReadAllBytes(pkgA).AsSpan().SequenceEqual(File.ReadAllBytes(pkgB)),
            "the two synthetic packages differ in bytes — this arm is not exercising a cache HIT");
        Assert.NotEqual(new FileInfo(pkgA).LastWriteTimeUtc.Ticks, new FileInfo(pkgB).LastWriteTimeUtc.Ticks);
        Assert.NotEqual(pkgA, pkgB);

        var keyA = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkgA), resolutionFailure: null);
        var keyB = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkgB), resolutionFailure: null);

        AssertIsWorkspaceKey(keyA);
        Assert.Equal(keyA, keyB);
    }

    /// <summary>
    /// A resolved closure that GAINS a package must not share a key with one that does not have
    /// it — the "dependency closure missing from the key entirely" shape #2754's own comment
    /// records one cache layer over. Declared identity is identical in both calls here; only
    /// what actually resolved differs.
    /// </summary>
    [SkippableFact]
    public void AResolvedClosureWithAnExtraPackage_ProducesADifferentWorkspaceKey()
    {
        TestArtifacts.SkipIfMissing();

        var (dir, ids) = WriteSourceApp("src-closure");

        var pkgA = WritePackage("closure-a", fill: (byte)'A');
        var extra = WritePackage("closure-extra", fill: (byte)'X', name: "Vendored Extra");

        var one = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkgA), resolutionFailure: null);
        var two = ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { dir }, ids,
            Resolved(pkgA).Concat(ResolvedNamed(extra, "Vendored Extra", new Guid("2846c003-1111-4222-8333-444455556666"))).ToList(),
            resolutionFailure: null);

        AssertIsWorkspaceKey(one);
        AssertIsWorkspaceKey(two);
        Assert.NotEqual(one, two);
    }

    /// <summary>
    /// Regression guard for what the key already did: an edited source file still moves it, and
    /// an unchanged input still reproduces it byte for byte. A change that keyed only on the
    /// resolved packages would pass every arm above and break this one.
    /// </summary>
    [SkippableFact]
    public void SourceContentStillDecidesTheKey_AndTheKeyIsDeterministic()
    {
        TestArtifacts.SkipIfMissing();

        var (dir, ids) = WriteSourceApp("src-edit");
        var pkg = WritePackage("edit-pkg", fill: (byte)'A');

        var before = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkg), resolutionFailure: null);
        var again = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkg), resolutionFailure: null);
        Assert.Equal(before, again);

        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), """
        codeunit 60730 "Workspace Key Probe"
        {
            procedure Probe(): Integer
            begin
                exit(2);
            end;
        }
        """);

        var after = ProgramSupport.ComputeSourceWorkspaceKey(new[] { dir }, ids, Resolved(pkg), resolutionFailure: null);
        AssertIsWorkspaceKey(after);
        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// A failed resolve must key on the FAILURE, never collapse to "no dependencies" — the rule
    /// GetOrderedDepIds states for the AL-output key, and for the same reason: an empty closure
    /// is indistinguishable from a bundle that genuinely has none, so a bundle whose dependency
    /// is temporarily missing would otherwise share a workspace directory with one that has no
    /// dependencies at all. It must also move when the reason moves.
    ///
    /// <para>RunLayeredPrePass catches the resolve so it can compute this key and rethrows from
    /// where it threw before; this arm is what makes that catch honest rather than a swallow.</para>
    /// </summary>
    [SkippableFact]
    public void AFailedResolve_KeysOnTheFailure_AndNotOnAnEmptyClosure()
    {
        TestArtifacts.SkipIfMissing();

        var (dir, ids) = WriteSourceApp("src-unresolved");
        var pkg = WritePackage("unresolved-pkg", fill: (byte)'A');

        var empty = Array.Empty<(AppManifest Manifest, string AppPath)>();
        var failedA = ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { dir }, ids, empty, "MissingDependencyException: LSC Chain Base not found");
        var failedB = ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { dir }, ids, empty, "MissingDependencyException: a different package not found");
        var genuinelyEmpty = ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { dir }, ids, empty, resolutionFailure: null);
        var resolved = ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { dir }, ids, Resolved(pkg), resolutionFailure: null);

        AssertIsWorkspaceKey(failedA);
        // A failure is not "no deps", and it is not a successful resolve either.
        Assert.NotEqual(genuinelyEmpty, failedA);
        Assert.NotEqual(resolved, failedA);
        // Two different reasons are two different cache identities.
        Assert.NotEqual(failedA, failedB);
        // ...and the same reason is stable, or the directory would never be reused.
        Assert.Equal(failedA, ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { dir }, ids, empty, "MissingDependencyException: LSC Chain Base not found"));
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    private static void AssertIsWorkspaceKey(string key)
    {
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    private (string Dir, IReadOnlyDictionary<string, BundleIdentity> Ids) WriteSourceApp(string name)
    {
        var dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{SourceAppId}}",
          "name": "Workspace Key Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepAppId}}", "name": "{{DepName}}", "publisher": "{{DepPublisher}}", "version": "1.0.0.0" }
          ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), """
        codeunit 60730 "Workspace Key Probe"
        {
            procedure Probe(): Integer
            begin
                exit(1);
            end;
        }
        """);

        var id = new BundleIdentity(
            SourceAppId, "Workspace Key Fixture", "AL Runner",
            new Version(1, 0, 0, 0), new Version(14, 0, 0, 0),
            new[] { new DependencyRef(DepAppId, DepName, DepPublisher, DepVersion) });

        var ids = new Dictionary<string, BundleIdentity>(StringComparer.OrdinalIgnoreCase) { [dir] = id };
        return (dir, ids);
    }

    /// <summary>
    /// A file of a fixed length filled with <paramref name="fill"/>, written at its OWN
    /// directory. The content hash the key consults is a plain SHA-256 over the file's bytes
    /// (BcAppSymbolCache.ComputeAppContentHash → RunnerFingerprint.ComputeContentHash), which
    /// never parses the package — so the manifest is not what this fixture needs to get right.
    /// What it does need to get right is a distinct path per package, because that hash is
    /// memoized per path for the process.
    /// </summary>
    private string WritePackage(string subdir, byte fill, string name = DepName)
    {
        var dir = Path.Combine(_scratch, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DepPublisher}_{name}_1.0.0.0.app");
        var bytes = new byte[8192];
        bytes.AsSpan().Fill(fill);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void StampSameMtime(string a, string b)
    {
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, stamp);
        File.SetLastWriteTimeUtc(b, stamp);
    }

    private static List<(AppManifest Manifest, string AppPath)> Resolved(string appPath)
        => ResolvedNamed(appPath, DepName, DepAppId);

    private static List<(AppManifest Manifest, string AppPath)> ResolvedNamed(string appPath, string name, Guid appId)
        => new()
        {
            (new AppManifest(DepPublisher, name, DepVersion, appId, Array.Empty<DependencyRef>()), appPath),
        };
}
