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

    internal static int CountDeclaringLiterals(string csSource) =>
        CSharpSource.CountStringLiterals(csSource, t => PlatformProperty.IsMatch(t));

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

    // The mechanism differs per entry (a compile error, a bundle that never runs, a request that
    // returns no events), so it belongs in the entry's own reason, never in this shared text.
    private const string Subject = "the floor is the subject";

    private const string Measured ="measured on #5112: a completed run with the floor removed fails the class";

    /// <summary>Checked-in fixture manifests that keep the floor, with the reason. Paths relative to AlRunner.Tests/.</summary>
    private static readonly Dictionary<string, string> AllowedFixtures = new()
    {
        ["Fixtures/BcFloorSkip/healthy-suite/app.json"] = "the floor is the subject (BcVersionFloorSkipTests)",
        ["Fixtures/BcFloorSkip/future-suite/app.json"] = "the floor is the subject (BcVersionFloorSkipTests)",
        ["Fixtures/CrossMajorNote/app.json"] = "the floor is the subject (CrossMajorNoteTests, #2210)",
        ["Fixtures/SubscriberScanAudit/app.json"] = "the floor is the subject (EventSubscriberScanEquivalenceTests)",
        ["Fixtures/ActiveSessionTable/app.json"] = "AL reads the System tables Active Session and Session (" + Measured + "; ActiveSessionTableTests: AL0185 Table 'Active Session' is missing)",
        ["Fixtures/SessionVirtualTable/app.json"] = "AL reads the System tables Session and Active Session (" + Measured + "; SessionVirtualTableTests: AL0185 Table 'Session' and Table 'Active Session' are missing)",
        ["Fixtures/TimeZoneVirtualTable/app.json"] = "AL reads the System table Time Zone (" + Measured + "; TimeZoneVirtualTableTests: AL0185 Table 'Time Zone' is missing)",
        ["Fixtures/WindowsLanguageVirtualTable/app.json"] = "AL reads the System table Windows Language (" + Measured + "; WindowsLanguageVirtualTableTests: AL0185 Table 'Windows Language' is missing)",
        ["Fixtures/FeatureKeyVirtualTable/app.json"] = "AL reads the System table Feature Key (" + Measured + "; FeatureKeyVirtualTableTests: AL0185 Table 'Feature Key' is missing)",
        ["Fixtures/EventSubscriptionVirtualTable/app.json"] = "AL reads the System table Event Subscription (" + Measured + "; EventSubscriptionVirtualTableTests and EventSubscriptionInventoryLazinessTests: AL0185 Table 'Event Subscription' is missing)",
        ["Fixtures/CodeunitMetadataVirtualTable/app.json"] = "AL reads the System table CodeUnit Metadata (" + Measured + "; CodeunitMetadataVirtualTableTests: AL0185 Table 'CodeUnit Metadata' is missing)",
        ["Fixtures/AggregatePermissionSet/app.json"] = "AL reads Aggregate Permission Set and Tenant Permission Set and declares a PermissionSet (" + Measured + "; AggregatePermissionSetVirtualTableTests and PermissionMetadataPopulationTests: AL0185 Table 'Aggregate Permission Set' and Table 'Tenant Permission Set' are missing)",
        ["Fixtures/ReportLayoutObsoleteExcelSource/app.json"] = "AL reads the System table Report Layout List (" + Measured + "; ReportLayoutObsoleteExcelSourceTests: AL0185 Table 'Report Layout List' is missing)",
        ["Fixtures/WatchEmitRegistriesReload/app.json"] = "AL reads the System table Report Layout List (" + Measured + "; WatchEmitRegistriesReloadTests: AL0185 Table 'Report Layout List' is missing)",
        ["Fixtures/EventSubscriptionMultiBundle/AppA/app.json"] = "AL reads the System tables Event Subscription and AllObj (" + Measured + "; EventSubscriptionMultiBundleScopeTests, with the floor removed from AppA alone: AL0185 Table 'Event Subscription' is missing)",
        ["Fixtures/EventSubscriptionMultiBundle/AppB/app.json"] = "AL reads the System tables Event Subscription and AllObj (" + Measured + "; EventSubscriptionMultiBundleScopeTests, with the floor removed from AppB alone: AL0185 Table 'Event Subscription' is missing)",
        ["Fixtures/SessionUserRowAlreadyPresent/dep/app.json"] = "AL reads the System table User and the session identity (" + Measured + "; SessionUserRowRefusalTests: AL0185 Table 'User' is missing, a source dependency that does not compile)",
        ["Fixtures/SessionUserRowNameCollision/dep/app.json"] = "AL reads the System tables User and User Property (" + Measured + "; SessionUserRowRefusalTests: AL0185 Table 'User' and Table 'User Property' are missing, a source dependency that does not compile)",
        ["Fixtures/InstallTriggerSessionIdentity/dep/app.json"] = "AL reads the System tables User and Access Control (" + Measured + "; InstallTriggerSessionIdentityTests and ServerBundleInstallBaselineReuseTests.SeedThatAdoptsAnotherSessionUser_IsNotReused fail; the seed app does not compile, AL0185 Table 'User' is missing)",
        ["Fixtures/DepInstallTriggerSessionIdentity/dep/app.json"] = "AL reads the System tables User, Access Control, Company and NAV App Installed App (" + Measured + "; DepInstallTriggerSessionIdentityTests fails; the observer app does not compile, AL0185 Table 'User', 'Company', 'Access Control' and 'NAV App Installed App' are missing)",
        ["Fixtures/BundleInstallTriggerSeedVisibility/main/app.json"] = "AL reads the System tables Access Control, Company and Published Application (" + Measured + "; BundleInstallTriggerSeedVisibilityTests: AL0185 Table 'Company', 'Access Control' and 'Published Application' are missing, EMIT-EXCLUDED)",
    };

    /// <summary>
    /// C# test sources that keep writing the floor into a manifest they generate, with the reason.
    /// Paths relative to AlRunner.Tests/ with '/' separators.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSources = new()
    {
        // The floor is the subject.
        ["BaseAppFloorFixtureGuardTests.cs"] = "the floor is the subject: its synthetic manifests exercise the floor matcher itself",
        ["DefaultBcVersionFloorTests.cs"] = "the floor is the subject: the default BC version follows the declared floor",
        ["DefaultBcVersionSupportedVariantTests.cs"] = "the floor is the subject: the default BC version follows the declared floor",
        ["DeferredPlatformAppsProvisioningTests.cs"] = "the floor is the subject: deferred platform-app provisioning",
        ["DeferredPlatformAppsWithholdTests.cs"] = "the floor is the subject: a platform-only bundle withholds the Microsoft closure",
        ["DeferredAttemptDependencyFloorTests.cs"] = "the floor is the subject: the withheld attempt cannot supply the Platform floor the default Test Runner declares (#5233)",
        ["DependencyResolverTests.cs"] = "the floor is the subject: the packaged floor is read back by DependencyResolver (#3719)",
        ["PlaceholderFloorProvisioningTests.cs"] = "the floor is the subject: the placeholder 1.0.0.0 floor",
        ["ServerBcFloorSkipTests.cs"] = "the floor is the subject: one app declares a future 999.0.0.0 floor, a parameter of the test (#5137)",
        ["NestedBundleManifestDiscoveryTests.cs"] = "the floor is the subject: one app declares a future 999.0.0.0 floor, a parameter of the test",
        ["PhaseLogIntegrationTests.cs"] = "the floor is the subject: its platformRoots parameter toggles the floor on purpose, to see the phase log with and without it",

        // Measured: fails without the floor because the AL needs System symbols.
        ["AllObjPopulateCostTests.cs"] = "only the manifest the server-mode test writes (WriteFixture with systemSymbols: true) keeps it: the server test's AL reads the System virtual tables AllObj and AllObjWithCaption and returns no events without the floor (" + Measured + "); the CLI tests on the same fixture pass without it, as do the file's other two manifests",
        ["AppGroupObjectVisibilityTests.cs"] = "AL reads the System tables AllObj, Table Metadata and XmlPort Metadata (AL0185 Table is missing), and both the WriteApp and the WriteGroup manifests fail without the floor (" + Measured + ")",
        ["InstallBaselineVirtualTableExclusionTests.cs"] = "AL reads AllObj, Field, Table Metadata and Page Metadata (" + Measured + ")",
        ["ObjectSystemTableEmptyRowSetTests.cs"] = "AL reads the System tables Object and AllObj (" + Measured + ")",
        ["ParentManifestNotReadSubprocessTests.cs"] = "the parent manifest's folder fixture reads the System table AllObj and fails without the floor (" + Measured + "); the own-manifest manifest passes without it",
        ["RecordLinkColumnsEndToEndTests.cs"] = "AL reads the System table Record Link (" + Measured + ")",
        ["RecordLinkCompanyFilterEndToEndTests.cs"] = "AL reads the System table Record Link (" + Measured + ")",
        ["InstallSeedClosure.cs"] = "the seed app's install trigger writes the System table Record Link and the bundle reads it (" + Measured + "); the two extra-dependency manifests pass without it",
        ["BackupReaderFailureReportingTests.cs"] = "--test-data maps the backup onto the System symbols, and without the floor the run resolves none (EXEC-FAIL: no Microsoft/ISV .app dependencies carrying a SymbolReference.json; " + Measured + ")",
        ["CliMultiPathBundleRunTests.cs"] = "AL reads AllObj, Table Metadata and XmlPort Metadata (" + Measured + "; AL0185 Table is missing)",
        ["CoverageMultiObjectFileTests.cs"] = "two of its manifests belong to bundles whose AL has `using System.Utilities;`, which fails AL0791 (namespace unknown) without the floor (" + Measured + "); the other three pass without it",
        ["CurrPageUpdateRefreshTests.cs"] = "its page fixture takes the System table Integer as SourceTable (" + Measured + "; AL0185 Table 'Integer' is missing)",
        ["MissingTestDataDiagnosisTests.cs"] = "--test-data maps the backup onto the System symbols, and without the floor the run resolves none (EXEC-FAIL: no Microsoft/ISV .app dependencies carrying a SymbolReference.json; " + Measured + ")",
        ["MissingTestDataSeededSingletonTests.cs"] = "--test-data maps the backup onto the System symbols, and without the floor the run resolves none (EXEC-FAIL: no Microsoft/ISV .app dependencies carrying a SymbolReference.json; " + Measured + ")",
        ["ReportLayoutFileResolutionTests.cs"] = "AL reads the System table Integer (" + Measured + "; AL0185 Table 'Integer' is missing)",
        ["ServerAffectedSelectionObjectKindTests.cs"] = "its Report reads the System table Integer as a data item, so every manifest in the class fails without the floor (AL0185 Table 'Integer' is missing, EMIT-EXCLUDED; " + Measured + ")",
        ["ServerAffectedSelectionUnknownRecordTests.cs"] = "the TriggerBundle manifest's trigger store subscribes to the System codeunit Global Triggers (AL0118 the name does not exist; " + Measured + "); the other manifests pass without it",
        ["ServerBundleInstallBaselineReuseTests.cs"] = "the PublishedVersionIsTheManifestVersion variant reads the System table Published Application and its test fails without the floor (" + Measured + "); the other variants pass without it",
        ["TestPageSourceTableTemporaryIntegerTests.cs"] = "its page fixtures take the System table Integer as SourceTable (" + Measured + "; AL0185 Table 'Integer' is missing)",
        ["WriteTransactionTestBoundaryTests.cs"] = "the report-and-page-field fixture reads the System table Integer (" + Measured + "; AL0185 Table 'Integer' is missing); the file's other three manifests pass without it",

        ["RecordLinkStoreEndToEndTests.cs"] = "AL reads the System table Record Link (" + Measured + ")",
        ["StaleBundleSymbolAppOwnershipTests.cs"] = "AL reads the System tables AllObj and Published Application (" + Measured + "; AL0185 Table is missing)",
    };

    /// <summary>
    /// How many string literals in an allowlisted source still declare the floor, when that is not
    /// one. An entry that only said "this file may declare it" would let a re-added manifest ride
    /// the entry of a file that had been stripped down to the one that genuinely needs it.
    /// </summary>
    private static readonly Dictionary<string, int> SourceDeclarationCounts = new()
    {
        ["AppGroupObjectVisibilityTests.cs"] = 2,
        ["BaseAppFloorFixtureGuardTests.cs"] = 4,
        ["CoverageMultiObjectFileTests.cs"] = 2,
        ["InstallSeedClosure.cs"] = 2,
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
    public void EverySourceEntry_PinsHowManyManifestLiteralsStillDeclareTheFloor()
    {
        var wrong = new List<string>();
        foreach (var rel in AllowedSources.Keys)
        {
            var path = Path.Combine(TestsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue; // reported by the stale-entry fact
            var expected = SourceDeclarationCounts.GetValueOrDefault(rel, 1);
            var actual = CountDeclaringLiterals(File.ReadAllText(path));
            if (actual != expected) wrong.Add($"{rel}: {actual} literal(s) declare the floor, the allowlist pins {expected}");
        }

        Assert.True(wrong.Count == 0,
            "A source's allowlist entry covers exactly the manifests that need the floor. Remove the property from the extra "
            + "manifest, or pin the new count in SourceDeclarationCounts WITH the reason it needs it:\n  "
            + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// "AL reads the System table X" was first judged by reading (#5112) and
    /// a read answer is not enough: a hand-run can pass without the floor while the suite's own run
    /// (DefaultTestToolPin) fails. An entry keeps the floor only because a completed run under
    /// <c>dotnet test</c> failed without it, or because the floor is the subject.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_SaysTheFloorIsTheSubject_OrCarriesAMeasuredBasis()
    {
        var unbased = AllowedFixtures.Concat(AllowedSources)
            .Where(e => !e.Value.Contains(Subject, StringComparison.Ordinal)
                        && !e.Value.Contains(Measured, StringComparison.Ordinal))
            .Select(e => $"{e.Key} ({e.Value})")
            .ToList();

        Assert.True(unbased.Count == 0,
            "These allowlist entries keep the floor on a reading, not a measurement. Strip the property, run the class "
            + "to completion under dotnet test (engine bootstrapped, never a hand-run), and either drop the floor or "
            + "add the object and diagnostic that failed together with Measured:\n  "
            + string.Join("\n  ", unbased));
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
    public void TwoManifestLiteralsInOneSource_CountAsTwoDeclarations()
    {
        Assert.Equal(2, CountDeclaringLiterals(Sub("""
            public class F
            {
                public string A() => "{ \"id\": \"a\", EPROP: \"1.0.0.0\" }";
                public string B() => "{ \"id\": \"b\" }";
                public string C() => "{ \"id\": \"c\", EPROP: \"27.0.0.0\" }";
                // No PROP: "1.0.0.0" here.
            }
            """)));
        Assert.Equal(0, CountDeclaringLiterals("public class F { public string A() => \"{ }\"; }"));
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
