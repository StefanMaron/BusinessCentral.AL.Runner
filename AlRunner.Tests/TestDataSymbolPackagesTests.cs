// TestDataSymbolPackagesTests — issue #2794.
//
// --test-data hands every resolved .app to the backup reader as `--symbols`. A source sibling
// (app + tests, both from source) is resolved through the runner's own workspace-deps NAVX —
// NavxManifest.xml + src/*.al, no SymbolReference.json, on purpose — and the reader refuses
// the whole list on that one entry ("no SymbolReference.json and no single inner .app"), so
// the run EXEC-FAILs before hydrating anything. The partition below is the fix's decision:
// only packages that carry a SymbolReference.json go to the reader; the rest are named on
// stderr, never silently dropped and never forwarded.

using System.IO.Compression;
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

    // ── the real predicate, against real files ─────────────────────────────────────────────

    private const string ManifestXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<Package xmlns=\"http://schemas.microsoft.com/navx/2015/manifest\">"
        + "<App Id=\"a2000001-0000-4000-8000-000000000001\" Name=\"ReproDepApp\" Publisher=\"Repro\" Version=\"1.0.0.0\" ShowMyCode=\"true\"/>"
        + "<Dependencies/></Package>";

    private static string WritePackage(string dir, string name, bool withSymbolReference)
    {
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("NavxManifest.xml").Open())) w.Write(ManifestXml);
        using (var w = new StreamWriter(zip.CreateEntry("src/A.Table.al").Open())) w.Write("table 50100 A { fields { field(1; K; Code[10]) { } } }");
        if (withSymbolReference)
            using (var w = new StreamWriter(zip.CreateEntry("SymbolReference.json").Open())) w.Write("{}");
        return path;
    }

    [Fact]
    public void SourceOnlyNavx_IsNotReaderConsumable()
    {
        var dir = Directory.CreateTempSubdirectory("al-runner-2794-").FullName;
        try
        {
            var p = WritePackage(dir, "Repro_ReproDepApp_1_0_0_0.app", withSymbolReference: false);
            Assert.False(TestDataProvisioner.IsReaderConsumable(p));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SymbolBearingNavx_IsReaderConsumable()
    {
        var dir = Directory.CreateTempSubdirectory("al-runner-2794-").FullName;
        try
        {
            var p = WritePackage(dir, "Microsoft_Something.app", withSymbolReference: true);
            Assert.True(TestDataProvisioner.IsReaderConsumable(p));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A file that is not a package at all stays with the reader — it, not the runner,
    /// says what is wrong with it. This is also the shape TestDataLazyLoadPolicyTests feeds in.</summary>
    [Fact]
    public void UnreadableFile_IsLeftForTheReader()
    {
        var dir = Directory.CreateTempSubdirectory("al-runner-2794-").FullName;
        try
        {
            var p = Path.Combine(dir, "Fake_App_1_0_0_0.app");
            File.WriteAllBytes(p, new byte[8]);
            Assert.True(TestDataProvisioner.IsReaderConsumable(p));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
