// PrecompileSupportTests — pins PrecompileSupport.WidenPackageCacheDirs (issue #2131).
//
// Proves the "search path is too narrow" fix directly: an EXPLICIT --package-cache dir
// list that omits the selected version's own runner-owned platform-apps/test-apps dirs
// must still see them widened in, when they exist on disk — and must NOT gain a phantom
// entry for a directory that does not exist. No BC engine, no BcArtifacts process-global
// state touched; this is pure filesystem + list logic.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompileSupportTests
{
    private static string NewTempArtifactsRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "precompile-support-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WidenPackageCacheDirs_AddsExistingPlatformAndTestAppsDirsNotAlreadyPresent()
    {
        var artifactsRoot = NewTempArtifactsRoot();
        try
        {
            const string version = "28.1.49838.53910";
            var platformAppsDir = Path.Combine(artifactsRoot, version, "platform-apps");
            var testAppsDir = Path.Combine(artifactsRoot, version, "test-apps");
            Directory.CreateDirectory(platformAppsDir);
            Directory.CreateDirectory(testAppsDir);

            var explicitlyRequested = new List<string> { "/some/caller-supplied/package-cache" };

            var widened = PrecompileSupport.WidenPackageCacheDirs(explicitlyRequested, artifactsRoot, version);

            // The caller's own dir is preserved (never dropped)...
            Assert.Contains("/some/caller-supplied/package-cache", widened);
            // ...and BOTH canonical dirs the caller never mentioned are added — this is
            // exactly the AL1022 "System Application is missing" gap #2131 reports: the
            // caller passed only test-apps and System Application (in platform-apps) was
            // never in the search set at all.
            Assert.Contains(platformAppsDir, widened);
            Assert.Contains(testAppsDir, widened);
            Assert.Equal(3, widened.Count);
        }
        finally
        {
            Directory.Delete(artifactsRoot, recursive: true);
        }
    }

    [Fact]
    public void WidenPackageCacheDirs_NeverAddsADirectoryThatDoesNotExistOnDisk()
    {
        // No artifacts root created at all — the negative direction: a version that was
        // never provisioned must not conjure a phantom search-path entry the caller can
        // silently rely on (Directory.EnumerateFiles over a missing dir already throws
        // elsewhere in this codebase's .app scanners if this ever regressed).
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "precompile-support-tests-missing-" + Guid.NewGuid().ToString("N"));
        const string version = "28.1.49838.50794";

        var widened = PrecompileSupport.WidenPackageCacheDirs(Array.Empty<string>(), artifactsRoot, version);

        Assert.Empty(widened);
        Assert.False(Directory.Exists(Path.Combine(artifactsRoot, version, "platform-apps")));
    }

    [Fact]
    public void WidenPackageCacheDirs_DoesNotDuplicateADirTheCallerAlreadyIncluded()
    {
        var artifactsRoot = NewTempArtifactsRoot();
        try
        {
            const string version = "28.1.49838.53910";
            var platformAppsDir = Path.Combine(artifactsRoot, version, "platform-apps");
            Directory.CreateDirectory(platformAppsDir);

            var alreadyIncluded = new List<string> { platformAppsDir };

            var widened = PrecompileSupport.WidenPackageCacheDirs(alreadyIncluded, artifactsRoot, version);

            Assert.Single(widened, d => d == platformAppsDir);
        }
        finally
        {
            Directory.Delete(artifactsRoot, recursive: true);
        }
    }
}
