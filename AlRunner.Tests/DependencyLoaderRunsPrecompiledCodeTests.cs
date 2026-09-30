// DependencyLoaderRunsPrecompiledCodeTests — issue #5051.
//
// DependencyLoader.RunsPrecompiledCode decides whether the dep-metadata producer compiles a
// dependency with the run's --define (the runner compiles its source, Tier 3) or without it (the
// runner loads compiled code, Tier 1/2). DependencyMetadataPrecompiledDefineTests drives the
// Tier-1 arm end to end; this pins each arm of the predicate on synthesized packages, including
// Tier 2, which no fixture there reaches.

using System.IO.Compression;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// CacheRoots is process-wide mutable static state (see CacheRootsSerialCollection's header);
// the R2R arm extracts its chunk into the r2r-chunks cache root.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyLoaderRunsPrecompiledCodeTests
{
    private static readonly AppManifest Dep = new(
        Publisher: "repro", Name: "precompiled-probe", Version: new Version(1, 0, 0, 0),
        AppId: Guid.Parse("5051c0de-1b2e-4f3e-9a41-5e2b8c9d0001"),
        Dependencies: Array.Empty<DependencyRef>());

    private static string NewDir(string prefix)
    {
        var dir = TestScratch.FlatDir(prefix);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WritePackage(string dir, params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(dir, "repro_precompiled-probe_1.0.0.0.app");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var (name, content) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(content, 0, content.Length);
        }
        return path;
    }

    private static readonly (string, byte[]) TableSource =
        ("src/Probe.Table.al", System.Text.Encoding.UTF8.GetBytes(
            "table 50200 Probe { fields { field(1; Code; Code[10]) { } } }"));

    private static void WithCacheRoot(Action body)
    {
        var cacheRoot = TestScratch.FlatDir("runs-precompiled-code-tests-");
        CacheRoots.SetOverride(cacheRoot);
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try { body(); }
        finally
        {
            CacheRoots.ResetForTests();
            RunnerFingerprint.ClearFileContentHashMemoForTests();
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    /// <summary>
    /// The negative arm, and the control for the other two: a package that ships only AL
    /// source, with no sidecar DLL beside the bucket, is compiled by the runner.
    /// </summary>
    [Fact]
    public void SourceOnlyPackage_NoSidecar_IsNotPrecompiled()
    {
        WithCacheRoot(() =>
        {
            var bucket = NewDir("runs-precompiled-source-only-");
            var pkg = WritePackage(bucket, TableSource);
            Assert.False(DependencyLoader.RunsPrecompiledCode(Dep, pkg, bucket));
        });
    }

    /// <summary>Tier 1: the SAME source-only package, with the bucket's sidecar DLL present.</summary>
    [Fact]
    public void SidecarDllInTheBucket_IsPrecompiled()
    {
        WithCacheRoot(() =>
        {
            var bucket = NewDir("runs-precompiled-sidecar-");
            var pkg = WritePackage(bucket, TableSource);
            var depsBin = Directory.CreateDirectory(Path.Combine(bucket, ".deps-bin")).FullName;
            File.WriteAllBytes(Path.Combine(depsBin, "repro_precompiled-probe_1.0.0.0.dll"), new byte[] { 0x4D, 0x5A });
            Assert.True(DependencyLoader.RunsPrecompiledCode(Dep, pkg, bucket));
        });
    }

    /// <summary>Tier 2: the package carries a <c>publishedartifacts/*.dll</c> chunk beside its source.</summary>
    [Fact]
    public void R2rPackage_IsPrecompiled()
    {
        WithCacheRoot(() =>
        {
            var bucket = NewDir("runs-precompiled-r2r-");
            var chunk = new byte[512];
            chunk[0] = 0x4D; chunk[1] = 0x5A;
            var pkg = WritePackage(bucket, TableSource, ("publishedartifacts/net8.0/Probe.dll", chunk));
            Assert.True(DependencyLoader.RunsPrecompiledCode(Dep, pkg, bucket));
        });
    }
}
