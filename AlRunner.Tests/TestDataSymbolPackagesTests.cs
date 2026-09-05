// TestDataSymbolPackagesTests — issue #2794.
//
// --test-data hands every resolved .app to the backup reader as `--symbols`. A source sibling
// (app + tests, both from source) is resolved through the runner's own workspace-deps NAVX —
// NavxManifest.xml + src/*.al, no SymbolReference.json, on purpose — and the reader refuses
// the whole list on that one entry ("no SymbolReference.json and no single inner .app"), so
// the run EXEC-FAILs before hydrating anything. The partition below is the fix's decision:
// only packages that carry a SymbolReference.json go to the reader; the rest are named on
// stderr, never silently dropped and never forwarded.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataSymbolPackagesTests
{
    private static bool HasSymbols(string p) => !p.Contains("workspace-deps", StringComparison.Ordinal);

    [Fact]
    public void SourceOnlyPackages_AreSkipped_AndNamed()
    {
        var apps = new[]
        {
            @"C:\cache\workspace-deps\abc\Repro_ReproDepApp_1_0_0_0.app",
            @"C:\platform-apps\Microsoft_Application.app",
            @"C:\platform-apps\Microsoft_Base Application.app",
        };

        var (keep, skipped) = TestDataProvisioner.PartitionSymbolPackages(apps, HasSymbols);

        Assert.Equal(new[]
        {
            @"C:\platform-apps\Microsoft_Application.app",
            @"C:\platform-apps\Microsoft_Base Application.app",
        }, keep);
        Assert.Equal(new[] { @"C:\cache\workspace-deps\abc\Repro_ReproDepApp_1_0_0_0.app" }, skipped);
    }

    [Fact]
    public void AllPackagesCarrySymbols_NothingSkipped_OrderPreserved()
    {
        var apps = new[] { @"C:\p\b.app", @"C:\p\a.app" };

        var (keep, skipped) = TestDataProvisioner.PartitionSymbolPackages(apps, _ => true);

        Assert.Equal(apps, keep);
        Assert.Empty(skipped);
    }

    [Fact]
    public void OnlySourceOnlyPackages_LeavesNothingForTheReader()
    {
        var (keep, skipped) = TestDataProvisioner.PartitionSymbolPackages(
            new[] { @"C:\cache\workspace-deps\x\A.app" }, HasSymbols);

        Assert.Empty(keep);
        Assert.Single(skipped);
    }
}
