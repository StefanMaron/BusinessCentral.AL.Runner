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
        _root = TestScratch.Dir("al-runner-edges");
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

    /// <summary>As <see cref="WriteApp"/>, with the App element's <c>Platform</c> attribute set —
    /// the only dependency Microsoft's test-toolkit packages declare (Library Assert's manifest:
    /// <c>Platform="28.0.0.0"</c>, empty <c>&lt;Dependencies /&gt;</c>).</summary>
    private static void WriteAppWithPlatform(
        string dir, string name, string publisher, string version, string platform,
        params (string Name, string Publisher)[] dependencies)
        => WriteAppWithFloors(dir, name, publisher, version, platform, application: null, dependencies);

    /// <summary>As <see cref="WriteApp"/>, with either or both floor attributes set.</summary>
    private static void WriteAppWithFloors(
        string dir, string name, string publisher, string version,
        string? platform, string? application,
        params (string Name, string Publisher)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d =>
            $"""    <Dependency Id="{Guid.NewGuid()}" Name="{d.Name}" Publisher="{d.Publisher}" MinVersion="{version}" />{"\n"}"""));
        var platformAttr = platform == null ? "" : $" Platform=\"{platform}\"";
        var applicationAttr = application == null ? "" : $" Application=\"{application}\"";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{Guid.NewGuid()}" Name="{name}" Publisher="{publisher}" Version="{version}"{applicationAttr}{platformAttr}/>
              <Dependencies>
            {deps}  </Dependencies>
            </Package>
            """;
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}.app"), WrapNavx(xml));
    }

    // ── #3719: a package's own Platform floor is an edge ──────────────────────

    [Fact]
    public void ScanDependencyEdges_PackageDeclaringPlatform_RecordsAnEdgeToSystem()
    {
        var dir = NewDir("floor-edge");
        WriteAppWithPlatform(dir, "Library Assert", "Microsoft", "28.1.49838.54169", platform: "28.0.0.0");

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.Equal(new[] { "System" }, edges["Library Assert"]);
    }

    /// <summary>
    /// #3875: a Microsoft platform app's PLATFORM floor IS recorded, because
    /// DependencyResolver.Visit now walks it — a scan that withheld the edge would tell
    /// provisioning a bundle needs no System.app for a closure resolution does pull it into,
    /// which is the direction that under-fetches rather than over-fetches. The two guards
    /// stay matched; what changed is what they guard.
    ///
    /// <para>Both directions here: a self-pointing floor (System declaring Platform, which
    /// must not become a self-edge) and a cross-pointing one (Base Application → System).</para>
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_PlatformAppsPlatformFloor_IsRecorded()
    {
        var dir = NewDir("floor-edge-platform");
        WriteAppWithPlatform(dir, "System", "Microsoft", "28.0.54265.0", platform: "28.0.54265.0");
        WriteAppWithPlatform(dir, "Base Application", "Microsoft", "28.1.49838.54169", platform: "28.0.0.0");

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.Equal(new[] { "System" }, edges["Base Application"]);
        // System's own Platform floor points at System: recorded, and harmless — the resolver
        // marks System done before the floor is visited, so the self-edge terminates there.
        Assert.Equal(new[] { "System" }, edges["System"]);
    }

    /// <summary>
    /// The other half of #3875's narrowing, and the one that keeps the cycle closed: a
    /// platform app's APPLICATION floor is still not recorded. No shipped build declares one
    /// (measured across six artifact builds, 27.3-28.4), and Microsoft/Application
    /// transitively names Base Application, so recording it would make provisioning demand a
    /// set resolution never walks.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_PlatformAppsApplicationFloor_IsNotRecorded()
    {
        var dir = NewDir("floor-edge-platform-application");
        WriteAppWithFloors(dir, "Base Application", "Microsoft", "28.1.49838.54169",
            platform: "28.0.0.0", application: "28.1.0.0");

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.DoesNotContain("Application", edges["Base Application"]);
        Assert.Equal(new[] { "System" }, edges["Base Application"]);
    }

    /// <summary>
    /// The Application floor is recorded too, not only Platform — an implementation that
    /// handled `Platform` alone would pass every other fact here. Microsoft's test packages
    /// really do declare it: Tests-ERM's manifest is Platform="28.0.0.0" Application="28.1.0.0".
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_PackageDeclaringApplication_RecordsAnEdgeToApplication()
    {
        var dir = NewDir("floor-edge-application");
        WriteAppWithFloors(dir, "Tests-ERM", "Microsoft", "28.1.49838.54169",
            platform: "28.0.0.0", application: "28.1.0.0", ("Library Assert", "Microsoft"));

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.Equal(new[] { "Library Assert", "Application", "System" }, edges["Tests-ERM"]);
    }

    /// <summary>
    /// The end-to-end need: a bundle naming only Library Assert, with the real-shaped toolkit
    /// package on disk, requires System — derived from that package's Platform floor, in the
    /// same second round that learns Application Test Library from Tests-TestLibraries. Without
    /// the edge the bundle downloaded test-apps alone and Library Assert's source compile died
    /// with EMIT-ZERO on the first run.
    /// </summary>
    [Fact]
    public void DetermineManifestNeeds_LibraryAssertOnDisk_RequiresSystemThroughItsPlatformFloor()
    {
        var dir = NewDir("floor-need");
        WriteAppWithPlatform(dir, "Library Assert", "Microsoft", "28.1.49838.54169", platform: "28.0.0.0");
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Library Assert", version: "28.0.0.0") }, edges);

        Assert.True(needs.NeedsTestApps);
        Assert.Equal(new[] { "System" }, needs.RequiredPlatformApps);
    }

    // -- #3794: a floor is a real dependency, with a real version, on BOTH sides --
    //
    // #3793 taught DependencyResolver.Visit to follow a resolved package's own
    // Platform/Application floors. Provisioning did not learn the same thing, in two
    // separate ways, and the floor's VERSION was discarded on both sides:
    //
    //   1. ScanDependencyEdges dropped every non-Microsoft package before reading it, and
    //      DetermineManifestNeeds only started its walk at Microsoft-published roots -- so a
    //      platform-less consumer depending on a source-bearing Contoso/Library whose
    //      manifest declares Platform="28.0.0.0" reported no platform need, downloaded
    //      nothing, and the Tier-3 compile of that library died on the same generic
    //      EMIT-ZERO as #3719.
    //   2. Only the edge's NAME was recorded, never its MinVersion, and DetermineVersionFloors
    //      read only the bundle's own roots -- so an existing but too-old app satisfied
    //      FindMissingPlatformApps through a transitive edge. That is #2193, filed against
    //      exactly this line and closed by the same change.

    /// <summary>
    /// A NON-Microsoft package's floor is an edge. The publisher filter belongs on the edge
    /// TARGET (a third-party name can never collide usefully with a platform-app name), not
    /// on the SOURCE: the resolver follows every non-platform package's floors whatever its
    /// publisher, so a provisioning scan that reads only Microsoft sources answers a
    /// different question from the one resolution asks.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_NonMicrosoftPackageDeclaringPlatform_IsRecordedAsAnEdgeSource()
    {
        var dir = NewDir("floor-edge-thirdparty");
        WriteAppWithPlatform(dir, "Library", "Contoso", "1.0.0.0", platform: "28.0.0.0");

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        // Keyed Publisher/Name, so it can never occupy or satisfy a Microsoft goal node.
        Assert.Equal(new[] { "System" }, edges["Contoso/Library"]);
        Assert.False(edges.ContainsKey("Library"));
    }

    /// <summary>
    /// The floor's declared VERSION reaches EdgeRequirements too, not only an ordinary
    /// &lt;Dependency&gt;'s MinVersion — a separate code path, since a floor is synthesized by
    /// AppLoader.ImplicitRoots rather than parsed from the Dependencies block.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_ImplicitFloor_CarriesItsDeclaredVersion()
    {
        var dir = NewDir("floor-edge-version");
        WriteAppWithFloors(dir, "Library Assert", "Microsoft", "28.1.49838.54169",
            platform: "28.0.0.0", application: "28.1.0.0");

        var reqs = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).EdgeRequirements["Library Assert"];

        Assert.Equal(new Version(28, 0, 0, 0),
            Assert.Single(reqs, r => r.Name == "System").Version);
        Assert.Equal(new Version(28, 1, 0, 0),
            Assert.Single(reqs, r => r.Name == "Application").Version);
    }

    /// <summary>
    /// The defect the #3810 review found: a non-Microsoft INTERMEDIATE vertex was dropped, so
    /// `Consumer -> Contoso/A -> Fabrikam/B (Platform=28.0)` recorded B's floor and then had no
    /// path from A to reach it with. Only the walk's GOALS are Microsoft-only; intermediate
    /// vertices cannot be filtered by publisher, because the resolver does not filter them.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_ThirdPartyIntermediate_IsTraversableToAMicrosoftGoal()
    {
        var dir = NewDir("floor-edge-intermediate");
        WriteApp(dir, "A", "Contoso", "1.0.0.0", ("B", "Fabrikam"));
        WriteAppWithPlatform(dir, "B", "Fabrikam", "1.0.0.0", platform: "28.0.0.0");
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.Equal(new[] { "Fabrikam/B" }, edges["Contoso/A"]);

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("A", publisher: "Contoso", version: "1.0.0.0") }, edges);

        Assert.Equal(new[] { "System" }, needs.RequiredPlatformApps);
    }

    /// <summary>
    /// The same two hops for a FLOOR, which is what #2193's closure walks: the version has to
    /// survive the third-party intermediate as well as the name.
    /// </summary>
    [Fact]
    public void DetermineVersionFloors_ThroughAThirdPartyIntermediate_KeepsTheFloor()
    {
        var dir = NewDir("floor-intermediate-version");
        WriteApp(dir, "A", "Contoso", "1.0.0.0", ("B", "Fabrikam"));
        WriteAppWithDependencyVersions(dir, "B", "Fabrikam", "1.0.0.0",
            ("Application Test Library", "Microsoft", "28.2.0.0"));
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        var floors = ProvisioningCheck.DetermineVersionFloors(
            new[] { Root("A", publisher: "Contoso", version: "1.0.0.0") }, scan.EdgeRequirements);

        Assert.Equal(new Version(28, 2, 0, 0), floors["Application Test Library"]);
        // A third-party name never becomes a floor: floors are looked up by plain name against
        // the curated Microsoft sets, and FindMissingPlatformApps would never ask for these.
        Assert.False(floors.ContainsKey("B"));
        Assert.False(floors.ContainsKey("Fabrikam/B"));
    }

    /// <summary>
    /// An R2R package's IMPLICIT floors are not edges (#3810 review): a floor exists to be
    /// compiled against, Tier-2 serves an R2R payload without compiling, and demanding the
    /// 116 MB platform download for a package nothing will compile is a cost with no failure
    /// behind it. Its declared &lt;Dependencies&gt; are recorded either way — those are runtime
    /// dependencies, not symbols — which is the half that keeps this from being a blanket
    /// exemption.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_R2RPackage_ContributesItsDependenciesButNotItsFloors()
    {
        var dir = NewDir("floor-edge-r2r");
        WriteR2RAppWithPlatform(dir, "Precompiled", "Contoso", "1.0.0.0", platform: "28.0.0.0",
            ("Application Test Library", "Microsoft"));

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.Equal(new[] { "Application Test Library" }, edges["Contoso/Precompiled"]);
    }

    /// <summary>The source-bearing twin of the fact above, same fixture but no payload — so
    /// the exemption is the R2R payload talking and not the floor being ignored outright.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_SourceBearingPackage_ContributesItsFloors()
    {
        var dir = NewDir("floor-edge-nor2r");
        WriteAppWithPlatform(dir, "Precompiled", "Contoso", "1.0.0.0", platform: "28.0.0.0",
            ("Application Test Library", "Microsoft"));

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        Assert.Equal(new[] { "Application Test Library", "System" }, edges["Contoso/Precompiled"]);
    }

    /// <summary>
    /// The same for an ordinary declared &lt;Dependency&gt;: a third-party package naming a
    /// Microsoft app is an edge into the platform set, and a third-party target is kept as an
    /// intermediate vertex under its qualified key.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_NonMicrosoftSource_KeepsEveryTargetUnderItsOwnKey()
    {
        var dir = NewDir("floor-edge-thirdparty-deps");
        WriteApp(dir, "Library", "Contoso", "1.0.0.0",
            ("Application", "Microsoft"), ("Other Contoso Thing", "Contoso"));

        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        // Both targets are kept, each under its own key. Only the walk's GOALS are
        // Microsoft-only; a third-party target is an intermediate vertex and dropping it
        // severed real paths (#3810 review).
        Assert.Equal(new[] { "Application", "Contoso/Other Contoso Thing" }, edges["Contoso/Library"]);
    }

    /// <summary>
    /// The walk must start at a non-Microsoft root too. Without this the edge recorded above
    /// is never consulted: DetermineManifestNeeds skipped every root whose publisher was not
    /// Microsoft before asking ReachesAnyOf anything.
    /// </summary>
    [Fact]
    public void DetermineManifestNeeds_NonMicrosoftRootReachingSystem_RequiresThePlatformSet()
    {
        var dir = NewDir("floor-need-thirdparty");
        WriteAppWithPlatform(dir, "Library", "Contoso", "1.0.0.0", platform: "28.0.0.0");
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Library", publisher: "Contoso", version: "1.0.0.0") }, edges);

        Assert.True(needs.NeedsPlatformApps);
        Assert.Equal(new[] { "System" }, needs.RequiredPlatformApps);
        // The platform set is not the test toolkit, and needing one must not drag the other
        // along: that is a separate 20 MB download this root says nothing about.
        Assert.False(needs.NeedsTestApps);
    }

    /// <summary>
    /// A third-party root that reaches nothing Microsoft stays out of the platform set --
    /// the negative direction, so the fix above cannot be "every root needs everything".
    /// </summary>
    [Fact]
    public void DetermineManifestNeeds_NonMicrosoftRootReachingNothing_RequiresNoPlatformSet()
    {
        var dir = NewDir("floor-need-thirdparty-none");
        WriteApp(dir, "Library", "Contoso", "1.0.0.0");
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { dir }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Library", publisher: "Contoso", version: "1.0.0.0") }, edges);

        Assert.False(needs.NeedsPlatformApps);
        Assert.Empty(needs.RequiredPlatformApps);
    }

    // -- #2193: the edge's declared MinVersion, closed over the graph --

    /// <summary>
    /// The scan records each edge's declared <c>MinVersion</c>, not just the target name.
    /// #2193: Tests-TestLibraries' manifest declares
    /// <c>&lt;Dependency Name="Application Test Library" MinVersion="28.1.0.0" /&gt;</c>, and
    /// discarding that number is why a warm cache holding 28.0 satisfied a bundle that
    /// transitively requires 28.1.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_RecordsTheDeclaredMinVersionOfEachEdge()
    {
        var dir = NewDir("edge-minversion");
        WriteAppWithDependencyVersions(dir, "Tests-TestLibraries", "Microsoft", "28.1.49838.54169",
            ("Application Test Library", "Microsoft", "28.1.0.0"));

        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        var atl = Assert.Single(
            scan.EdgeRequirements["Tests-TestLibraries"],
            r => string.Equals(r.Name, "Application Test Library", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new Version(28, 1, 0, 0), atl.Version);
    }

    /// <summary>
    /// A floor declared on a package the bundle only reaches TRANSITIVELY is a floor. The
    /// bundle names Tests-TestLibraries &gt;= 28.1.0.0 and nothing else; the floor for
    /// Application Test Library has to come off that package's own manifest.
    /// </summary>
    [Fact]
    public void DetermineVersionFloors_TransitiveEdge_ClosesTheFloorOverTheGraph()
    {
        var dir = NewDir("floor-transitive");
        WriteAppWithDependencyVersions(dir, "Tests-TestLibraries", "Microsoft", "28.1.49838.54169",
            ("Application Test Library", "Microsoft", "28.1.0.0"));
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        var floors = ProvisioningCheck.DetermineVersionFloors(
            new[] { Root("Tests-TestLibraries", version: "28.1.0.0") }, scan.EdgeRequirements);

        Assert.Equal(new Version(28, 1, 0, 0), floors["Application Test Library"]);
        Assert.Equal(new Version(28, 1, 0, 0), floors["Tests-TestLibraries"]);
    }

    /// <summary>
    /// Where a root and a traversed edge disagree, the HIGHER floor wins -- the same rule the
    /// root-only map already applied across bundles. Both directions, so an implementation
    /// that simply overwrites cannot pass: the root is stricter here, the edge stricter in
    /// the fact above.
    /// </summary>
    [Fact]
    public void DetermineVersionFloors_RootStricterThanTheEdge_KeepsTheRootFloor()
    {
        var dir = NewDir("floor-transitive-max");
        WriteAppWithDependencyVersions(dir, "Tests-TestLibraries", "Microsoft", "28.1.49838.54169",
            ("Application Test Library", "Microsoft", "28.0.0.0"));
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        var floors = ProvisioningCheck.DetermineVersionFloors(
            new[]
            {
                Root("Tests-TestLibraries", version: "28.1.0.0"),
                Root("Application Test Library", version: "28.2.0.0"),
            },
            scan.EdgeRequirements);

        Assert.Equal(new Version(28, 2, 0, 0), floors["Application Test Library"]);
    }

    /// <summary>
    /// A cycle in the manifest graph terminates rather than hanging, matching
    /// <see cref="ProvisioningCheck.ReachesAnyOf"/>'s own guarantee. Real Microsoft platform
    /// manifests reference each other, so this is the ordinary case, not an exotic one.
    /// </summary>
    [Fact]
    public void DetermineVersionFloors_CyclicEdges_Terminate()
    {
        var dir = NewDir("floor-transitive-cycle");
        WriteAppWithDependencyVersions(dir, "A", "Microsoft", "28.0.0.0", ("B", "Microsoft", "28.1.0.0"));
        WriteAppWithDependencyVersions(dir, "B", "Microsoft", "28.0.0.0", ("A", "Microsoft", "28.3.0.0"));
        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        var floors = ProvisioningCheck.DetermineVersionFloors(
            new[] { Root("A", version: "28.0.0.0") }, scan.EdgeRequirements);

        Assert.Equal(new Version(28, 1, 0, 0), floors["B"]);
        Assert.Equal(new Version(28, 3, 0, 0), floors["A"]);
    }

    /// <summary>
    /// End to end, the shape #2193 reported: a warm cache whose Application Test Library is
    /// 28.0 while Tests-TestLibraries transitively requires 28.1. The app is present, so a
    /// presence-only check calls the set complete and downloads nothing; the floor makes it
    /// missing, which is what a download can then fix.
    /// </summary>
    [Fact]
    public void DecideManifestProvisioning_TransitivelyBelowFloorApp_CountsAsMissing()
    {
        var dir = NewDir("floor-transitive-decision");
        WriteAppWithDependencyVersions(dir, "Tests-TestLibraries", "Microsoft", "28.1.49838.54169",
            ("Application Test Library", "Microsoft", "28.1.0.0"));
        WriteR2RApp(dir, "Application Test Library", "Microsoft", "28.0.46665.53459");
        var legacy = ProvisioningCheck.CheckPlatformApps("28.1.49838.53910", new[] { dir });

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries", version: "28.1.0.0") }, legacy, new[] { dir });

        Assert.Contains("Application Test Library", decision.MissingPlatformApps);
        Assert.False(decision.PlatformComplete);
    }

    /// <summary>The same cache one build newer than the transitive floor is complete -- the
    /// negative direction, so "missing" above is the floor talking and not the scan failing
    /// to see the app at all.</summary>
    [Fact]
    public void DecideManifestProvisioning_TransitiveFloorSatisfied_CountsAsPresent()
    {
        var dir = NewDir("floor-transitive-decision-ok");
        WriteAppWithDependencyVersions(dir, "Tests-TestLibraries", "Microsoft", "28.1.49838.54169",
            ("Application Test Library", "Microsoft", "28.1.0.0"));
        WriteR2RApp(dir, "Application Test Library", "Microsoft", "28.1.49838.54169");
        var legacy = ProvisioningCheck.CheckPlatformApps("28.1.49838.53910", new[] { dir });

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries", version: "28.1.0.0") }, legacy, new[] { dir });

        Assert.DoesNotContain("Application Test Library", decision.MissingPlatformApps);
    }

    /// <summary>As <see cref="WriteApp"/>, with a per-dependency MinVersion rather than the
    /// package's own version -- which is what a real manifest declares, and what #2193 is
    /// about.</summary>
    private static void WriteAppWithDependencyVersions(
        string dir, string name, string publisher, string version,
        params (string Name, string Publisher, string MinVersion)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d =>
            $"""    <Dependency Id="{Guid.NewGuid()}" Name="{d.Name}" Publisher="{d.Publisher}" MinVersion="{d.MinVersion}" />{"\n"}"""));
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

    /// <summary>As <see cref="WriteApp"/>, but the package carries a
    /// <c>publishedartifacts/</c> entry — i.e. an R2R runtime package rather than a
    /// symbol-only one, which is what a real provisioned platform-apps dir holds.</summary>
    private static void WriteR2RApp(
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
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}.app"),
            WrapNavx(xml, includePublishedArtifact: true));
    }

    /// <summary>As <see cref="WriteR2RApp"/>, with the App element's <c>Platform</c>
    /// attribute set.</summary>
    private static void WriteR2RAppWithPlatform(
        string dir, string name, string publisher, string version, string platform,
        params (string Name, string Publisher)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d =>
            $"""    <Dependency Id="{Guid.NewGuid()}" Name="{d.Name}" Publisher="{d.Publisher}" MinVersion="{version}" />{"\n"}"""));
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{Guid.NewGuid()}" Name="{name}" Publisher="{publisher}" Version="{version}" Platform="{platform}"/>
              <Dependencies>
            {deps}  </Dependencies>
            </Package>
            """;
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}.app"),
            WrapNavx(xml, includePublishedArtifact: true));
    }

    private static byte[] WrapNavx(string manifestXml, bool includePublishedArtifact = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using (var es = entry.Open())
                es.Write(Encoding.UTF8.GetBytes(manifestXml));
            if (includePublishedArtifact)
            {
                var dll = zip.CreateEntry("publishedartifacts/app.dll");
                using var ds = dll.Open();
                ds.Write(new byte[] { 0x4D, 0x5A });
            }
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

    /// <summary>
    /// #3794 inverted the SOURCE half of this: a third-party package is now an edge source,
    /// because DependencyResolver.Visit follows its floors and &lt;Dependencies&gt; whatever
    /// its publisher, and a scan blind to it made provisioning answer a different question
    /// from resolution. The node key is what keeps that unambiguous: this package occupies
    /// `Contoso ISV/Contoso Extension`, never the bare name a Microsoft app could hold.
    /// </summary>
    [Fact]
    public void ScanDependencyEdges_NonMicrosoftPackage_IsAnEdgeSourceUnderItsQualifiedKey()
    {
        var dir = NewDir("isv");
        WriteApp(dir, "Contoso Extension", "Contoso ISV", "1.0.0.0",
            ("Application Test Library", "Microsoft"), ("Contoso Helper", "Contoso ISV"));

        var scan = ProvisioningCheck.ScanDependencyEdges(new[] { dir });

        Assert.False(scan.Edges.ContainsKey("Contoso Extension"));
        Assert.Equal(
            new[] { "Application Test Library", "Contoso ISV/Contoso Helper" },
            scan.Edges["Contoso ISV/Contoso Extension"].ToArray());
    }

    /// <summary>
    /// A third-party app sharing a Microsoft app's NAME occupies a different node, in BOTH
    /// directory orders — the #3810 review's point that a one-order test passes a "last one
    /// wins" implementation as readily as the right one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanDependencyEdges_ThirdPartyAppNamedLikeAMicrosoftOne_DoesNotShadowIt(bool microsoftFirst)
    {
        var msDir = NewDir($"shadow-ms-{microsoftFirst}");
        var isvDir = NewDir($"shadow-isv-{microsoftFirst}");
        WriteApp(isvDir, "Application", "Contoso ISV", "1.0.0.0", ("Contoso Helper", "Contoso ISV"));
        WriteApp(msDir, "Application", "Microsoft", "28.1.49838.54169", ("Base Application", "Microsoft"));

        var dirs = microsoftFirst ? new[] { msDir, isvDir } : new[] { isvDir, msDir };
        var scan = ProvisioningCheck.ScanDependencyEdges(dirs);

        Assert.Equal(new[] { "Base Application" }, scan.Edges["Application"].ToArray());
        Assert.Equal(new[] { "Contoso ISV/Contoso Helper" }, scan.Edges["Contoso ISV/Application"].ToArray());
    }

    /// <summary>
    /// Two builds of ONE app: the higher version's edges are the operative ones, in both
    /// directory orders. #3810's review found this: first-directory-wins was harmless while
    /// the graph only answered "does this reach a platform app", and stopped being harmless
    /// when the edges started carrying floors — a cache with B v28.1 (needing Application Test
    /// Library 28.0) ahead of B v28.2 (needing 28.2) recorded the 28.0 floor while the
    /// resolver selects v28.2 and enforces 28.2, which is #2193's stale cache all over again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanDependencyEdges_TwoBuildsOfOneApp_TheHigherVersionsEdgesWin(bool newerFirst)
    {
        var older = NewDir($"ver-old-{newerFirst}");
        var newer = NewDir($"ver-new-{newerFirst}");
        WriteAppWithDependencyVersions(older, "B", "Microsoft", "28.1.0.0",
            ("Application Test Library", "Microsoft", "28.0.0.0"));
        WriteAppWithDependencyVersions(newer, "B", "Microsoft", "28.2.0.0",
            ("Application Test Library", "Microsoft", "28.2.0.0"));

        var dirs = newerFirst ? new[] { newer, older } : new[] { older, newer };
        var scan = ProvisioningCheck.ScanDependencyEdges(dirs);

        var floors = ProvisioningCheck.DetermineVersionFloors(
            new[] { Root("B", version: "28.2.0.0") }, scan.EdgeRequirements);

        Assert.Equal(new Version(28, 2, 0, 0), floors["Application Test Library"]);
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
    public void DetermineManifestNeeds_Bc27Edges_TestsTestLibraries_DoesNotDemandApplicationTestLibrary()
    {
        // THE version-specific bug the hand table shipped: on BC 27.x this demanded an
        // "Application Test Library" no 27.x artifact contains, and provisioning then died
        // with "still missing after download".
        //
        // Issue #2205 broadened the need targets from that one app to the whole curated
        // platform-apps set, so the claim worth pinning is now the precise one, not
        // NeedsPlatformApps as a blanket. On 27.x the recorded edges reach System
        // Application (via System Application Test Library) and Business Foundation (via
        // Business Foundation Test Libraries) — both of which the 27.x w1 artifact really
        // does ship — and they must NOT reach Application Test Library.
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { WriteBc27TestToolkit() }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Tests-TestLibraries", version: "27.5.0.0") }, edges);

        Assert.DoesNotContain("Application Test Library", needs.RequiredPlatformApps);
        Assert.Equal(
            new[] { "Business Foundation", "System Application" },
            needs.RequiredPlatformApps.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.True(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_Bc28Edges_TestsTestLibraries_DoesDemandPlatformApps()
    {
        var edges = ProvisioningCheck.ScanDependencyEdges(new[] { WriteBc28TestToolkit() }).Edges;

        var needs = ProvisioningCheck.DetermineManifestNeeds(
            new[] { Root("Tests-TestLibraries", version: "28.3.0.0") }, edges);

        Assert.True(needs.NeedsPlatformApps);
        Assert.Contains("Application Test Library", needs.RequiredPlatformApps);
        Assert.True(needs.NeedsTestApps);
    }

    [Fact]
    public void DecideManifestProvisioning_Bc27ToolkitOnDisk_DoesNotAskForApplicationTestLibrary()
    {
        // Only the test-apps set is on disk here, so the platform apps the 27.x edges DO
        // reach are genuinely absent and a platform fetch is the right answer (issue
        // #2205). What must never come back is Application Test Library: no 27.x artifact
        // ships it, so demanding it makes the download unsatisfiable.
        var dir = WriteBc27TestToolkit();
        var legacy = ProvisioningCheck.CheckPlatformApps("27.5.46862.48827", new[] { dir });

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries", version: "27.5.0.0") }, legacy, new[] { dir });

        Assert.DoesNotContain("Application Test Library", decision.RequiredPlatformApps);
        Assert.DoesNotContain("Application Test Library", decision.MissingPlatformApps);
        Assert.True(decision.NeedsTestApps);
    }

    [Fact]
    public void DecideManifestProvisioning_Bc27ToolkitAndPlatformAppsOnDisk_NoPlatformDownload()
    {
        // The warm 27.x machine (what CI's --package-cache gives every leg): the same
        // roots, with the platform apps the edges reach also present, must still decide
        // "no download". This is the no-spurious-download arm for the edge-derived half of
        // the requirement, the counterpart of the manifest-declared half in
        // ProvisioningCheckTests.DecideManifestProvisioning_WarmCache_OrdinaryAlApp_NoDownload.
        var toolkit = WriteBc27TestToolkit();
        var platform = NewDir("platform-apps-27");
        WriteR2RApp(platform, "System Application", "Microsoft", "27.5.0.0");
        WriteR2RApp(platform, "Business Foundation", "Microsoft", "27.5.0.0");

        var dirs = new[] { toolkit, platform };
        var legacy = ProvisioningCheck.CheckPlatformApps("27.5.46862.48827", dirs);

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            new[] { Root("Tests-TestLibraries", version: "27.5.0.0") }, legacy, dirs);

        Assert.Empty(decision.MissingPlatformApps);
        Assert.True(decision.PlatformComplete);
        Assert.False(decision.ShouldDownloadPlatform);
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
