// ManifestDependencyEdgeScanTests — issue #2103.
//
// The Microsoft-app dependency edges the provisioning pre-scan walks used to be a
// hand-written table (`ProvisioningCheck.KnownMicrosoftAppDependencyEdges`), transcribed
// once from a BC 28.3 `NavxManifest.xml`. A hand table is version-blind by construction,
// and Microsoft genuinely moves apps between packages across BC releases — measured while
// fixing this issue:
//
//   BC 27.0 / 27.5   Tests-TestLibraries -> System Application Test Library,
//                                           Library Variable Storage,
//                                           Permissions Mock,
//                                           Business Foundation Test Libraries
//                    (NO "Application Test Library" — that app does not exist on 27.x at
//                     all; it is absent from the w1 Extensions set for 27.0/27.3/27.5.)
//
//   BC 28.0/28.1/28.3  Tests-TestLibraries -> System Application Test Library,
//                                             Permissions Mock,
//                                             Application Test Library
//
// The hand table recorded only the 28.x shape, so on BC 27.x it claimed a dependency that
// does not exist and drove provisioning to demand an app no 27.x artifact ships.
//
// These tests pin the replacement: real edges read out of the `.app` packages already on
// disk. They assert CONCRETE named edges, both directions of the 27-vs-28 split, and that
// an unreadable package is reported rather than silently collapsing the graph to empty
// (.claude/rules/loud-failures.md).

using System.IO.Compression;
using System.Text;
using Xunit;
using AlRunner;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class ManifestDependencyEdgeScanTests : IDisposable
{
    private readonly string _root;

    public ManifestDependencyEdgeScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-edges", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private string NewDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes a NAVX `.app` whose NavxManifest.xml declares the given dependencies —
    /// the same shape <see cref="AppLoader.ReadManifest"/> parses out of a real Microsoft
    /// package.</summary>
    private static void WriteApp(
        string dir, string name, string publisher, string version,
        params (string Name, string Publisher)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d =>
            $"""    <Dependency Id="{Guid.NewGuid()}" Name="{d.Name}" Publisher="{d.Publisher}" MinVersion="{version}" />{"\n"}"""));
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{Guid.NewGuid()}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
              <Dependencies>
            {deps}  </Dependencies>
            </Package>
            """;
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}.app"), WrapNavx(xml));
    }

    private static byte[] WrapNavx(string manifestXml)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(manifestXml));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    /// <summary>The BC 28.x test-toolkit set, edges exactly as measured from the real
    /// 28.0.46665.53258 / 28.1.49838.50794 / 28.3.52162.53954 packages.</summary>
    private string WriteBc28TestToolkit()
    {
        var dir = NewDir("test-apps-28");
        WriteApp(dir, "Tests-TestLibraries", "Microsoft", "28.3.0.0",
            ("System Application Test Library", "Microsoft"),
            ("Permissions Mock", "Microsoft"),
            ("Application Test Library", "Microsoft"));
        WriteApp(dir, "System Application Test Library", "Microsoft", "28.3.0.0",
            ("System Application", "Microsoft"), ("Any", "Microsoft"));
        WriteApp(dir, "Business Foundation Test Libraries", "Microsoft", "28.3.0.0",
            ("System Application", "Microsoft"), ("Business Foundation", "Microsoft"));
        WriteApp(dir, "Library Variable Storage", "Microsoft", "28.3.0.0",
            ("Library Assert", "Microsoft"));
        WriteApp(dir, "Library Assert", "Microsoft", "28.3.0.0");
        WriteApp(dir, "Permissions Mock", "Microsoft", "28.3.0.0");
        WriteApp(dir, "Any", "Microsoft", "28.3.0.0");
        WriteApp(dir, "Test Runner", "Microsoft", "28.3.0.0");
        return dir;
    }

    /// <summary>The BC 27.x test-toolkit set, edges exactly as measured from the real
    /// 27.0.38460.53260 / 27.5.46862.48827 packages. Note the absence of any edge to
    /// "Application Test Library" — no 27.x artifact ships that app.</summary>
    private string WriteBc27TestToolkit()
    {
        var dir = NewDir("test-apps-27");
        WriteApp(dir, "Tests-TestLibraries", "Microsoft", "27.5.0.0",
            ("System Application Test Library", "Microsoft"),
            ("Library Variable Storage", "Microsoft"),
            ("Permissions Mock", "Microsoft"),
            ("Business Foundation Test Libraries", "Microsoft"));
        WriteApp(dir, "System Application Test Library", "Microsoft", "27.5.0.0",
            ("System Application", "Microsoft"), ("Any", "Microsoft"));
        WriteApp(dir, "Business Foundation Test Libraries", "Microsoft", "27.5.0.0",
            ("System Application", "Microsoft"), ("Business Foundation", "Microsoft"));
        WriteApp(dir, "Library Variable Storage", "Microsoft", "27.5.0.0",
            ("Library Assert", "Microsoft"));
        WriteApp(dir, "Library Assert", "Microsoft", "27.5.0.0");
        WriteApp(dir, "Permissions Mock", "Microsoft", "27.5.0.0");
        WriteApp(dir, "Any", "Microsoft", "27.5.0.0");
        WriteApp(dir, "Test Runner", "Microsoft", "27.5.0.0");
        return dir;
    }

    private static DependencyRef Root(string name, string publisher = "Microsoft", string version = "28.0.0.0")
        => new(Guid.NewGuid(), name, publisher, Version.Parse(version));

    // ── the scan itself ──────────────────────────────────────────────────────

    [Fact]
    public void ScanDependencyEdges_Bc28Packages_YieldsTheExactEdgesTheManifestsDeclare()
    {
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { WriteBc28TestToolkit() });

        Assert.Empty(scan.UnreadablePackages);
        Assert.Equal(
            new[] { "Application Test Library", "Permissions Mock", "System Application Test Library" },
            scan.Edges["Tests-TestLibraries"].OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "Any", "System Application" },
            scan.Edges["System Application Test Library"].OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "Library Assert" },
            scan.Edges["Library Variable Storage"].ToArray());
    }

    [Fact]
    public void ScanDependencyEdges_Bc27Packages_RecordNoApplicationTestLibraryEdge()
    {
        // The measured 27.x shape. "Application Test Library" must appear NOWHERE in the
        // graph — it is not a dependency of anything on 27.x because it does not exist there.
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { WriteBc27TestToolkit() });

        Assert.Equal(
            new[] { "Business Foundation Test Libraries", "Library Variable Storage",
                    "Permissions Mock", "System Application Test Library" },
            scan.Edges["Tests-TestLibraries"].OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(
            scan.Edges.Values.SelectMany(v => v),
            n => string.Equals(n, "Application Test Library", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanDependencyEdges_AppWithNoDependencies_IsRecordedWithAnEmptyEdgeList()
    {
        // Negative direction that matters: "scanned, declares nothing" must be a RECORDED
        // fact (key present, list empty), not indistinguishable from "never looked at".
        var dir = NewDir("no-deps");
        WriteApp(dir, "Library Assert", "Microsoft", "28.3.0.0");

        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        Assert.True(scan.Edges.ContainsKey("Library Assert"));
        Assert.Empty(scan.Edges["Library Assert"]);
        Assert.False(scan.Edges.ContainsKey("Test Runner"));
    }

    [Fact]
    public void ScanDependencyEdges_CorruptPackage_IsReportedAndDoesNotCollapseTheGraph()
    {
        // .claude/rules/loud-failures.md: a package the scan cannot read must be NAMED.
        // Silently yielding an empty graph would read as "this app needs nothing", which is
        // exactly the quiet wrong answer that makes a provisioning miss surface later,
        // somewhere unrelated.
        var dir = NewDir("corrupt");
        WriteApp(dir, "Tests-TestLibraries", "Microsoft", "28.3.0.0",
            ("Application Test Library", "Microsoft"));
        var bad = Path.Combine(dir, "Microsoft_Broken.app");
        File.WriteAllBytes(bad, new byte[] { 0x4E, 0x41, 0x56, 0x58, 0x08, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03 });

        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        Assert.Contains(bad, scan.UnreadablePackages);
        // The readable sibling still contributes — one bad file is not a whole-scan wipe.
        Assert.Equal(new[] { "Application Test Library" }, scan.Edges["Tests-TestLibraries"].ToArray());
    }

    [Fact]
    public void ScanDependencyEdges_MissingDirectory_YieldsNothingAndReportsNoFalseFailure()
    {
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { Path.Combine(_root, "never-created") });

        Assert.Empty(scan.Edges);
        Assert.Empty(scan.UnreadablePackages);
    }

    [Fact]
    public void ScanDependencyEdges_NestedPackage_IsFoundJustLikeThePresenceChecksFindIt()
    {
        // NoFallbackPlatformAppsPresent and TestToolkitPresent both walk the search dirs
        // with SearchOption.AllDirectories. If this scan looked only at the top level, an
        // app nested one dir down would read as PRESENT to those two and as EDGE-UNKNOWN
        // here — two answers about the same packages, from the same dirs, under different
        // rules. Same rules, or the disagreement is a bug waiting to be reported.
        var dir = NewDir("nested");
        var inner = Path.Combine(dir, "Extensions");
        Directory.CreateDirectory(inner);
        WriteApp(inner, "Tests-TestLibraries", "Microsoft", "28.3.0.0",
            ("Application Test Library", "Microsoft"));
        WriteApp(inner, ProvisioningCheck.TestToolkitSentinelApp, "Microsoft", "28.3.0.0",
            ("System Application", "Microsoft"), ("Business Foundation", "Microsoft"));

        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        Assert.Equal(new[] { "Application Test Library" }, scan.Edges["Tests-TestLibraries"].ToArray());
        // The sibling presence check finds the same nested tree — same rules, same answer.
        Assert.True(ProvisioningCheck.TestToolkitPresent(new[] { dir }));
    }

    [Fact]
    public void ScanDependencyEdges_NonMicrosoftPackage_IsNotRecordedAsAnEdgeSource()
    {
        var dir = NewDir("isv");
        WriteApp(dir, "Contoso Extension", "Contoso ISV", "1.0.0.0",
            ("Application Test Library", "Microsoft"));

        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        Assert.False(scan.Edges.ContainsKey("Contoso Extension"));
    }

    [Fact]
    public void ScanDependencyEdges_RepeatedCall_ReturnsTheSameEdgesOffTheWarmManifestCache()
    {
        // AppLoader.ReadManifest is backed by an in-process memo AND an on-disk index keyed
        // by (path, length, mtime). A warm second scan must produce the identical graph —
        // a cache-shaped defect here would be invisible to a single cold run.
        var dir = WriteBc28TestToolkit();

        var cold = ProvisioningCheck.ScanDependencyEdges(new[] { dir });
        var warm = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        Assert.Equal(
            cold.Edges.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + string.Join(",", kv.Value.OrderBy(v => v, StringComparer.Ordinal))),
            warm.Edges.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + string.Join(",", kv.Value.OrderBy(v => v, StringComparer.Ordinal))));
        Assert.Equal(
            new[] { "Application Test Library", "Permissions Mock", "System Application Test Library" },
            warm.Edges["Tests-TestLibraries"].OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // ── the decision that consumes it ────────────────────────────────────────

    [Fact]
    public void DetermineManifestNeeds_Bc27Edges_TestsTestLibraries_DoesNotDemandPlatformApps()
    {
        // THE version-specific bug the hand table shipped: on BC 27.x this returned
        // NeedsPlatformApps == true, sending provisioning after an "Application Test
        // Library" no 27.x artifact contains.
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { WriteBc27TestToolkit() }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Tests-TestLibraries", version: "27.5.0.0") }, edges);

        Assert.False(needs.NeedsPlatformApps);
        Assert.True(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_Bc28Edges_TestsTestLibraries_DoesDemandPlatformApps()
    {
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { WriteBc28TestToolkit() }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Tests-TestLibraries", version: "28.3.0.0") }, edges);

        Assert.True(needs.NeedsPlatformApps);
        Assert.True(needs.NeedsTestApps);
    }

    [Fact]
    public void DecideManifestProvisioning_Bc27ToolkitOnDisk_DoesNotAskForApplicationTestLibrary()
    {
        var dir = WriteBc27TestToolkit();
        var legacy = ProvisioningCheck.CheckPlatformApps("27.5.46862.48827", new[] { dir });

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries", version: "27.5.0.0") }, legacy, new[] { dir });

        Assert.False(decision.NeedsPlatformApps);
        Assert.False(decision.ShouldDownloadPlatform);
        Assert.True(decision.NeedsTestApps);
    }

    [Fact]
    public void DecideManifestProvisioning_Bc28ToolkitOnDisk_AsksForApplicationTestLibrary()
    {
        var dir = WriteBc28TestToolkit();
        var legacy = ProvisioningCheck.CheckPlatformApps("28.3.52162.53954", new[] { dir });

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries", version: "28.3.0.0") }, legacy, new[] { dir });

        Assert.True(decision.NeedsPlatformApps);
        Assert.True(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_ColdCache_LearnsThePlatformNeedOnceTheTestSetLands()
    {
        // Issue #2103's chicken-and-egg, resolved by ordering rather than by a hand table:
        // round 1 sees an empty cache and can only conclude "the test set is needed"; the
        // test set is what carries Tests-TestLibraries' OWN manifest, so round 2 — run
        // against the now-populated dir — derives the platform need from the real edges.
        var testAppsDir = NewDir("cold-test-apps");
        var roots = new[] { Root("Tests-TestLibraries", version: "28.3.0.0") };
        var legacy = ProvisioningCheck.CheckPlatformApps("28.3.52162.53954", new[] { testAppsDir });

        var round1 = ProvisioningCheck.DecideManifestProvisioning(roots, legacy, new[] { testAppsDir });
        Assert.True(round1.ShouldDownloadTest);
        Assert.False(round1.ShouldDownloadPlatform);

        // "download" the BC 28.x test set into that dir.
        foreach (var f in Directory.EnumerateFiles(WriteBc28TestToolkit(), "*.app"))
            File.Copy(f, Path.Combine(testAppsDir, Path.GetFileName(f)));

        var round2 = ProvisioningCheck.DecideManifestProvisioning(roots, legacy, new[] { testAppsDir });
        Assert.True(round2.NeedsPlatformApps);
        Assert.True(round2.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_UnreadablePackage_IsSurfacedOnTheDecision()
    {
        var dir = NewDir("decide-corrupt");
        WriteApp(dir, "Tests-TestLibraries", "Microsoft", "28.3.0.0",
            ("Application Test Library", "Microsoft"));
        var bad = Path.Combine(dir, "Microsoft_Broken.app");
        File.WriteAllBytes(bad, new byte[] { 0x4E, 0x41, 0x56, 0x58, 0x08, 0x00, 0x00, 0x00, 0xFF });

        var legacy = ProvisioningCheck.CheckPlatformApps("28.3.52162.53954", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries") }, legacy, new[] { dir });

        Assert.Contains(bad, decision.UnreadablePackages);
    }
}
