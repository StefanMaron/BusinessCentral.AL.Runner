// AppLoaderR2rChunkCacheTests — proves AppLoader.ExtractAllDllPaths (issue #perf-B):
// extracts a real R2R .app's publishedartifacts/*.dll chunks to a content-addressed cache
// directory ONCE and returns file paths, instead of re-inflating the zip into byte[] on
// every call the way ExtractAllDlls (still used internally on a cache MISS) does.
//
// Two decisive claims, each with its own proof:
//   1. The chunk(s) written to the cache dir are IDENTICAL in content to what the existing
//      byte[]-based ExtractAllDlls produces — proven via PE metadata identity (assembly
//      name + MVID read straight off the bytes/file via System.Reflection.Metadata, the
//      same low-risk technique RecordPatches.ReportMetadataVirtualTable.cs already uses
//      for report-id pre-warming) rather than Assembly.Load, which would load a real BC
//      platform assembly into THIS test process's default ALC twice over and risk
//      polluting shared AppDomain state other tests in the same collection depend on.
//   2. A second ExtractAllDllPaths call for the SAME file is a genuine cache HIT, not a
//      re-extract that happens to produce the same files — proven via
//      AppLoader.R2rExtractInvocationCountForTests, the same "did real work actually
//      happen" counter pattern BcAppSymbolCache/AppLoaderManifestCacheTests use.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// CacheRoots is process-wide mutable static state (see CacheRootsSerialCollection's header).
[Collection(CacheRootsSerialCollection.Name)]
public sealed class AppLoaderR2rChunkCacheTests
{
    private static (string? Name, Guid Mvid)? ReadPeIdentity(Stream peStream)
    {
        try
        {
            using var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen);
            if (!reader.HasMetadata) return null;
            var mr = reader.GetMetadataReader();
            var asmDef = mr.GetAssemblyDefinition();
            var name = mr.GetString(asmDef.Name);
            var mvid = mr.GetGuid(mr.GetModuleDefinition().Mvid);
            return (name, mvid);
        }
        catch { return null; }
    }

    private static List<(string? Name, Guid Mvid)> ReadPeIdentitiesFromBytes(IReadOnlyList<byte[]> dlls)
    {
        var result = new List<(string?, Guid)>();
        foreach (var dll in dlls)
        {
            using var ms = new MemoryStream(dll, writable: false);
            var id = ReadPeIdentity(ms);
            Assert.NotNull(id);
            result.Add(id!.Value);
        }
        return result.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    private static List<(string? Name, Guid Mvid)> ReadPeIdentitiesFromPaths(IReadOnlyList<string> paths)
    {
        var result = new List<(string?, Guid)>();
        foreach (var path in paths)
        {
            using var fs = File.OpenRead(path);
            var id = ReadPeIdentity(fs);
            Assert.NotNull(id);
            result.Add(id!.Value);
        }
        return result.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>Finds a small-but-real R2R platform .app to keep the test fast — "Business
    /// Foundation" is a single ~700KB publishedartifacts/*.dll chunk (verified locally),
    /// unlike Base Application's ~98MB/5-chunk package.</summary>
    private static string? FindSmallR2RPlatformApp(string platformDir)
        => Directory.Exists(platformDir)
            ? Directory.EnumerateFiles(platformDir, "*Business Foundation*.app").FirstOrDefault(AppLoader.IsR2R)
            : null;

    [SkippableFact]
    public void ExtractAllDllPaths_MatchesExtractAllDllsByPeIdentity_AndSecondCallIsACacheHit()
    {
        var platformDir = TestArtifacts.PlatformAppsDir();
        var appPath = FindSmallR2RPlatformApp(platformDir);
        TestArtifacts.SkipIf(appPath == null,
            $"No R2R 'Business Foundation' .app found under '{platformDir}' — provision platform apps first.");

        var cacheRoot = Path.Combine(Path.GetTempPath(), "app-loader-r2r-chunk-cache-tests-" + Guid.NewGuid().ToString("N"));
        CacheRoots.SetOverride(cacheRoot);
        try
        {
            // Baseline identity: the existing byte[]-based extraction path (unchanged).
            var byteDlls = AppLoader.ExtractAllDlls(appPath!);
            Assert.NotEmpty(byteDlls);
            var byteIdentities = ReadPeIdentitiesFromBytes(byteDlls);

            Assert.Equal(0, AppLoader.R2rExtractInvocationCountForTests(appPath!));

            // First ExtractAllDllPaths call: cache MISS, must genuinely extract.
            var paths1 = AppLoader.ExtractAllDllPaths(appPath!);
            Assert.Equal(byteDlls.Count, paths1.Count);
            Assert.Equal(1, AppLoader.R2rExtractInvocationCountForTests(appPath!));
            foreach (var p in paths1) Assert.True(File.Exists(p), $"expected cached chunk at {p}");

            // Claim 1: same PE identity (name + MVID) as the byte[] path — the cached copy
            // is genuinely the same content, not a placeholder or truncated write.
            var pathIdentities1 = ReadPeIdentitiesFromPaths(paths1);
            Assert.Equal(byteIdentities, pathIdentities1);

            // Claim 2: a second call for the SAME file is a cache HIT — no re-extract, same
            // file paths returned, and the completion marker + every chunk file are intact.
            var paths2 = AppLoader.ExtractAllDllPaths(appPath!);
            Assert.Equal(1, AppLoader.R2rExtractInvocationCountForTests(appPath!)); // still 1 — no re-extract
            Assert.Equal(paths1.OrderBy(p => p, StringComparer.Ordinal),
                         paths2.OrderBy(p => p, StringComparer.Ordinal));
            foreach (var p in paths2) Assert.True(File.Exists(p));

            var pathIdentities2 = ReadPeIdentitiesFromPaths(paths2);
            Assert.Equal(byteIdentities, pathIdentities2);
        }
        finally
        {
            CacheRoots.ResetForTests();
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [SkippableFact]
    public void ExtractAllDllPaths_UnknownOrNonR2RFile_ReturnsEmpty()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "app-loader-r2r-chunk-cache-tests-" + Guid.NewGuid().ToString("N"));
        CacheRoots.SetOverride(cacheRoot);
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".app");
            var result = AppLoader.ExtractAllDllPaths(missing);
            Assert.Empty(result);
        }
        finally
        {
            CacheRoots.ResetForTests();
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }
}
