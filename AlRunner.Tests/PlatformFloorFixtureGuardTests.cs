using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards #5112: a fixture manifest written by a test in AlRunner.Tests must not carry a
/// <c>"platform"</c> property unless it needs System symbols or the floor is the subject.
///
/// <c>platform</c> makes the runner provision and compile against the System symbols on every
/// invocation (about 0.97 s per process when the AL output is recompiled). Most fixtures
/// declared it out of habit; #5112 removed it from every one that passed without it. The rule
/// is mechanical, never by eye: remove the property, and the class must still compile and pass
/// with the same assertions. Anything that does not stays on an allowlist below WITH the reason,
/// and an entry that stops declaring it fails the stale-entry fact, so the lists only shrink.
///
/// Sibling of <see cref="BaseAppFloorFixtureGuardTests"/> (the <c>"application"</c> floor,
/// .claude/rules/no-base-app-in-csharp-tests.md) and shaped like it: checked-in fixture manifests
/// are read as parsed JSON, C# sources only inside their string literals, so a comment that
/// quotes the property is not a declaration.
/// </summary>
public sealed class PlatformFloorFixtureGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestsDir => Path.Combine(RepoRoot, "AlRunner.Tests");

    private static string Rel(string path) =>
        Path.GetRelativePath(TestsDir, path).Replace(Path.DirectorySeparatorChar, '/');

    // The key is composed at runtime: this file is itself scanned, and spelling the property
    // next to a colon in a literal would make it an offender of its own rule.
    private const string Key = "platform";

    private static readonly Regex PlatformProperty = new(
        @"\\?""" + Key + @"\\?""\s*:", RegexOptions.Compiled);

    private static IReadOnlyList<string> TestSourcePaths()
    {
        var paths = Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Rel(p).Split('/').Any(seg => seg is "bin" or "obj"))
            .ToList();
        Assert.True(paths.Count > 0, $"expected .cs sources under {TestsDir}, found none — the guard reads nothing.");
        return paths;
    }

    private static IReadOnlyList<string> FixtureManifestPaths()
    {
        var fixtures = Path.Combine(TestsDir, "Fixtures");
        var paths = Directory.EnumerateFiles(fixtures, "app.json", SearchOption.AllDirectories).ToList();
        Assert.True(paths.Count > 0, $"expected app.json fixtures under {fixtures}, found none — the guard reads nothing.");
        return paths;
    }

    /// <summary>True when <paramref name="fileName"/>'s content DECLARES the platform floor.</summary>
    internal static bool DeclaresPlatformFloor(string fileName, string text) =>
        Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ManifestDeclaresPlatform(text)
            : CSharpSource.AnyStringLiteral(text, t => PlatformProperty.IsMatch(t));

    private static bool DeclaresPlatformFloor(string path) =>
        DeclaresPlatformFloor(path, File.ReadAllText(path));

    // Matches the readers (Dependencies.ReadDependencies): a root string property, non-blank.
    // An unparseable manifest falls back to the raw scan and over-reports, never reads clean.
    private static bool ManifestDeclaresPlatform(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(Key, out var v)
                && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString());
        }
        catch (JsonException)
        {
            return PlatformProperty.IsMatch(text);
        }
    }

    private const string Measured =
        "measured on #5112: with the floor removed the class fails (bundle exits 3 / EMIT-EXCLUDED) because " +
        "its AL needs System symbols";

    private const string PendingSharedServer =
        "not yet measured: #5178 converts this class to the shared server, so the floor is dropped " +
        "after it merges (#5112 follow-up) rather than conflicting with it";

    /// <summary>Checked-in fixture manifests that keep the floor, with the reason. Paths relative to AlRunner.Tests/.</summary>
    private static readonly Dictionary<string, string> AllowedFixtures = new()
    {
        ["Fixtures/BcFloorSkip/healthy-suite/app.json"] = "the floor is the subject (BcVersionFloorSkipTests)",
        ["Fixtures/BcFloorSkip/future-suite/app.json"] = "the floor is the subject (BcVersionFloorSkipTests)",
        ["Fixtures/CrossMajorNote/app.json"] = "the floor is the subject (CrossMajorNoteTests, #2210)",
        ["Fixtures/SubscriberScanAudit/app.json"] = "the floor is the subject (EventSubscriberScanEquivalenceTests)",
        ["Fixtures/ActiveSessionTable/app.json"] = "AL reads the System tables Active Session and Session",
        ["Fixtures/SessionVirtualTable/app.json"] = "AL reads the System tables Session and Active Session",
        ["Fixtures/TimeZoneVirtualTable/app.json"] = "AL reads the System table Time Zone",
        ["Fixtures/WindowsLanguageVirtualTable/app.json"] = "AL reads the System table Windows Language",
        ["Fixtures/FeatureKeyVirtualTable/app.json"] = "AL reads the System table Feature Key",
        ["Fixtures/EventSubscriptionVirtualTable/app.json"] = "AL reads the System table Event Subscription",
        ["Fixtures/CodeunitMetadataVirtualTable/app.json"] = "AL reads the System tables Metadata and CodeUnit Metadata",
        ["Fixtures/AggregatePermissionSet/app.json"] = "AL reads Aggregate Permission Set and Tenant Permission Set and declares a PermissionSet",
        ["Fixtures/ReportLayoutObsoleteExcelSource/app.json"] = "AL reads the System table Report Layout List",
        ["Fixtures/WatchEmitRegistriesReload/app.json"] = "AL reads the System table Report Layout List",
        ["Fixtures/EventSubscriptionMultiBundle/AppA/app.json"] = "AL reads the System tables Event Subscription and AllObj",
        ["Fixtures/EventSubscriptionMultiBundle/AppB/app.json"] = "AL reads the System tables Event Subscription and AllObj",
        ["Fixtures/SessionUserRowAlreadyPresent/dep/app.json"] = "AL reads the System table User and the session identity",
        ["Fixtures/SessionUserRowAlreadyPresent/main/app.json"] = "AL reads the System table User and the session identity",
        ["Fixtures/SessionUserRowNameCollision/dep/app.json"] = "AL reads the System tables User and User Property",
        ["Fixtures/SessionUserRowNameCollision/main/app.json"] = "AL reads the System tables User and User Property",
        ["Fixtures/InstallTriggerSessionIdentity/dep/app.json"] = "AL reads the System tables User and Access Control",
        ["Fixtures/InstallTriggerSessionIdentity/main/app.json"] = "AL reads the session identity the install trigger keys on (System User and Access Control)",
        ["Fixtures/DepInstallTriggerSessionIdentity/dep/app.json"] = "AL reads the System tables User, Access Control, Company and NAV App Installed App",
        ["Fixtures/DepInstallTriggerSessionIdentity/main/app.json"] = "depends on the dep fixture that reads System tables; both bundles are one install closure",
        ["Fixtures/BundleInstallTriggerSeedVisibility/main/app.json"] = "AL reads the System tables Access Control, Company and Published Application",
    };

    /// <summary>
    /// C# test sources that keep writing the floor into a manifest they generate, with the reason.
    /// Paths relative to AlRunner.Tests/ with '/' separators.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSources = new()
    {
        // The floor is the subject.
        ["BaseAppFloorFixtureGuardTests.cs"] = "its synthetic manifests exercise the floor matcher itself",
        ["DefaultBcVersionFloorTests.cs"] = "the floor is the subject: the default BC version follows the declared floor",
        ["DefaultBcVersionSupportedVariantTests.cs"] = "the floor is the subject: the default BC version follows the declared floor",
        ["DeferredPlatformAppsProvisioningTests.cs"] = "the floor is the subject: deferred platform-app provisioning",
        ["DeferredPlatformAppsWithholdTests.cs"] = "the floor is the subject: a platform-only bundle withholds the Microsoft closure",
        ["DependencyResolverTests.cs"] = "the floor is the subject: the packaged floor is read back by DependencyResolver (#3719)",
        ["PlaceholderFloorProvisioningTests.cs"] = "the floor is the subject: the placeholder 1.0.0.0 floor",
        ["NestedBundleManifestDiscoveryTests.cs"] = "the floor is a parameter of the test: one app declares a future 999.0.0.0 floor",
        ["PhaseLogIntegrationTests.cs"] = "its platformRoots parameter toggles the floor on purpose, to see the phase log with and without it",
        ["ServerAffectedSelectionUnrecordedStateTests.cs"] = "the test-runner variant needs the 27.0.0.0 floor and a second test bumps the floor as the edit under test",

        // Measured: fails without the floor because the AL needs System symbols.
        ["AllObjPopulateCostTests.cs"] = "AL reads the System virtual tables AllObj and AllObjWithCaption (" + Measured + ")",
        ["AppGroupObjectVisibilityTests.cs"] = "AL reads AllObj, Table Metadata, Page Metadata, Report Metadata and Event Subscription (" + Measured + ")",
        ["InstallBaselineVirtualTableExclusionTests.cs"] = "AL reads AllObj, Field, Table Metadata and Page Metadata (" + Measured + ")",
        ["ObjectSystemTableEmptyRowSetTests.cs"] = "AL reads the System tables Object and AllObj (" + Measured + ")",
        ["ParentManifestNotReadSubprocessTests.cs"] = "AL reads the System table AllObj (" + Measured + ")",
        ["RecordLinkColumnsEndToEndTests.cs"] = "AL reads the System table Record Link (" + Measured + ")",
        ["RecordLinkCompanyFilterEndToEndTests.cs"] = "AL reads the System table Record Link (" + Measured + ")",
        ["InstallSeedClosure.cs"] = "the install-seed closure writes rows through the System table Record Link (" + Measured + ")",
        ["BackupReaderFailureReportingTests.cs"] = Measured,
        ["CliMultiPathBundleRunTests.cs"] = Measured,
        ["CoverageMultiObjectFileTests.cs"] = Measured,
        ["CurrPageUpdateRefreshTests.cs"] = Measured,
        ["MissingTestDataDiagnosisTests.cs"] = Measured,
        ["MissingTestDataSeededSingletonTests.cs"] = Measured,
        ["PartialCompanyInitAcceptanceEscalationTests.cs"] = Measured,
        ["ReportLayoutFileResolutionTests.cs"] = Measured,
        ["ServerAffectedSelectionObjectKindTests.cs"] = Measured,
        ["ServerAffectedSelectionUnknownRecordTests.cs"] = Measured,
        ["ServerBundleInstallBaselineReuseTests.cs"] = Measured,
        ["TestPageSourceTableTemporaryIntegerTests.cs"] = Measured,
        ["WriteTransactionTestBoundaryTests.cs"] = Measured,

        // Not yet attempted: #5178 owns these files until it merges.
        ["CrossBundleModuleIdentityDedupTests.cs"] = PendingSharedServer,
        ["FunctionIsolationInstanceReuseTests.cs"] = PendingSharedServer,
        ["HostOpenPartErrorTests.cs"] = PendingSharedServer,
        ["QueryFlowFieldColumnProjectionTests.cs"] = PendingSharedServer,
        ["QueryFlowFieldFlowFilterTests.cs"] = PendingSharedServer,
        ["QueryHavingAndJoinAggregationProjectionTests.cs"] = PendingSharedServer,
        ["QueryJoinFilterElementGroupByTests.cs"] = PendingSharedServer,
        ["QueryJoinFlowFieldColumnOosTests.cs"] = PendingSharedServer,
        ["QueryRangeFilterRetargetTests.cs"] = PendingSharedServer,
        ["QueryWildcardFilterProjectionTests.cs"] = PendingSharedServer,
        ["RecordLinkStoreEndToEndTests.cs"] = PendingSharedServer,
        ["SourceTableViewApplicationTests.cs"] = PendingSharedServer,
        ["StaleBundleSymbolAppOwnershipTests.cs"] = PendingSharedServer,
        ["SuiteServerTests.cs"] = PendingSharedServer,
        ["TestPagePartConstFilterLinkTests.cs"] = PendingSharedServer,
        ["TextSplitVariadicSeparatorsTests.cs"] = PendingSharedServer,
    };

    [Fact]
    public void NoFixtureManifest_DeclaresThePlatformFloor_ExceptTheAllowlisted()
    {
        var offenders = FixtureManifestPaths()
            .Where(DeclaresPlatformFloor)
            .Select(Rel)
            .Where(rel => !AllowedFixtures.ContainsKey(rel))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These fixture app.json files declare the platform floor without being on the allowlist in this file:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nDeclaring it makes the runner compile against the System symbols on every invocation (#5112). "
            + "Remove the property and run the class; if it then fails because the AL needs System symbols, "
            + "add the fixture here WITH that reason.");
    }

    [Fact]
    public void NoTestSource_WritesThePlatformFloor_ExceptTheAllowlisted()
    {
        var offenders = TestSourcePaths()
            .Where(DeclaresPlatformFloor)
            .Select(Rel)
            .Where(rel => !AllowedSources.ContainsKey(rel))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These test sources write the platform floor into a manifest without being on the allowlist in this file:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nSee #5112. Drop the property; keep it only with the reason the AL needs System symbols.");
    }

    /// <summary>A stale entry silently re-permits the next fixture that takes the name.</summary>
    [Fact]
    public void EveryAllowlistEntry_StillDeclaresTheFloor_SoTheListCannotGoStale()
    {
        var stale = new List<string>();
        foreach (var (rel, why) in AllowedFixtures.Concat(AllowedSources))
        {
            var path = Path.Combine(TestsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !DeclaresPlatformFloor(path))
                stale.Add($"{rel} ({why})");
        }

        Assert.True(stale.Count == 0,
            "These allowlist entries no longer declare the platform floor (or no longer exist). Delete them:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryAllowlistEntry_CarriesAReason()
    {
        var blank = AllowedFixtures.Concat(AllowedSources)
            .Where(e => string.IsNullOrWhiteSpace(e.Value) || e.Value.Trim().Length < 20)
            .Select(e => e.Key)
            .ToList();

        Assert.True(blank.Count == 0,
            "Allowlist entries need a real reason (at least 20 characters):\n  " + string.Join("\n  ", blank));
    }

    // ── what counts as DECLARING the floor — driven on synthetic content ────────────────────

    private static string Sub(string template) => template
        .Replace("EPROP", "\\\"" + Key + "\\\"")
        .Replace("PROP", "\"" + Key + "\"");

    [Fact]
    public void AManifestDeclaringTheFloorAtTheRoot_IsADeclaration() =>
        Assert.True(DeclaresPlatformFloor("app.json", Sub("""{ "id": "a", PROP: "1.0.0.0", "dependencies": [] }""")));

    [Fact]
    public void AManifestWithoutTheFloor_IsNotADeclaration() =>
        Assert.False(DeclaresPlatformFloor("app.json", """{ "id": "a", "dependencies": [] }"""));

    [Fact]
    public void TheKeyNestedInsideAnotherObject_IsNotTheRootFloor() =>
        Assert.False(DeclaresPlatformFloor("app.json",
            Sub("""{ "id": "a", "dependencies": [ { "name": "X", PROP: "1.0.0.0" } ] }""")));

    [Fact]
    public void ANullOrBlankFloor_IsNotADeclaration()
    {
        Assert.False(DeclaresPlatformFloor("app.json", Sub("""{ "id": "a", PROP: null }""")));
        Assert.False(DeclaresPlatformFloor("app.json", Sub("""{ "id": "a", PROP: " " }""")));
    }

    [Fact]
    public void AnUnparseableManifest_FallsBackToTheRawScan_RatherThanReadingClean() =>
        Assert.True(DeclaresPlatformFloor("app.json", Sub("""{ "id": "a", PROP: "1.0.0.0", }}}""")));

    [Fact]
    public void AManifestInAStringLiteral_IsADeclaration_InEveryLiteralForm()
    {
        Assert.True(DeclaresPlatformFloor("F.cs", Sub("""
            public class F { public string M() => "{ \"id\": \"a\", EPROP: \"1.0.0.0\" }"; }
            """)));
        Assert.True(DeclaresPlatformFloor("F.cs", Sub(""""
            public class F { public string M() => """
                { "id": "a", PROP: "1.0.0.0" }
                """; }
            """")));
        Assert.True(DeclaresPlatformFloor("F.cs", Sub("""
            public class F { public string M(string v) => $"{{ EPROP: \"{v}\" }}"; }
            """)));
    }

    [Fact]
    public void AProseCommentQuotingTheProperty_IsNotADeclaration() =>
        Assert.False(DeclaresPlatformFloor("F.cs", Sub("""
            public class F
            {
                // No PROP: "1.0.0.0" here.
                /* Nor PROP: "27.0.0.0". */
                public string M() => "{ \"id\": \"a\" }";
            }
            """)));

    /// <summary>
    /// Anchors the placeholder expansion to a real allowlisted manifest, so every synthetic fact
    /// above agrees with what a genuine app.json looks like.
    /// </summary>
    [Fact]
    public void ThePlaceholderExpansion_MatchesARealAllowlistedManifest()
    {
        var real = Path.Combine(TestsDir, "Fixtures", "TimeZoneVirtualTable", "app.json");
        Assert.True(File.Exists(real), $"{real} not found — was the fixture renamed?");
        Assert.Contains(Sub("PROP"), File.ReadAllText(real), StringComparison.Ordinal);
        Assert.True(DeclaresPlatformFloor(real));
    }
}
