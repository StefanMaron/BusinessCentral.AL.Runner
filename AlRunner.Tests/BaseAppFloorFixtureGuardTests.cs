using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards <c>.claude/rules/no-base-app-in-csharp-tests.md</c>: a fixture manifest written
/// by a test in AlRunner.Tests must not carry an <c>"application"</c> property.
///
/// The rule is measured, not stylistic. <c>"application"</c> is the Base Application
/// dependency, and it is not declared through <c>dependencies</c>, which is why it is easy
/// to add without noticing what it pulls in: from #2205 on, ReadDependencies synthesises
/// implicit Microsoft/Application + Microsoft/System roots from it, so every runner
/// subprocess spawned against that bundle resolves and loads the platform closure.
///
/// The rule listed four classes that still carried the floor and asked for no fifth. It
/// had no enforcement, and a fifth arrived anyway — not as a class, as a static fixture:
/// AlRunner.Tests/Fixtures/RecordTriggerXRec/app.json, a bundle with
/// <c>"dependencies": []</c> and its own private Assert codeunit, which needs nothing from
/// Microsoft at all. It is also the most-spawned fixture in the suite: the CI phase log for
/// run 33791744880 records 28 spawns of it per BC leg, 313 s of subprocess wall on the
/// 28.1 leg and 472 s on 28.2 — about a quarter of all subprocess time in the unit-test
/// step, for a property that bought nothing. Removing it took a warm invocation from
/// 4.65 s to 1.92 s locally, and this file's 14 dependent test classes from 4 m 20 s to
/// 1 m 54 s.
///
/// Removing the floor from RecordTriggerXRec broke one of its 14 dependent test classes:
/// EventSubscriberScanEquivalenceTests (in AssemblyTypeIndexTests.cs) drives the runner with
/// AL_RUNNER_SUBSCRIBER_SCAN_AUDIT=1 and asserts over 3,000 real [NavEventSubscriber]
/// methods across Base Application + System Application -- those assemblies only load
/// because the fixture used to declare the floor, and the test never declared its own need
/// for it. Rather than restore the floor to all 28-spawns-per-leg of RecordTriggerXRec, it
/// now has its own dedicated fixture, Fixtures/SubscriberScanAudit, whose entire purpose is
/// to carry the floor -- so it is paid once per leg instead of 28 times. Found by a
/// completed eight-leg CI run, not a partial local one -- see the note below on why that
/// distinction matters.
///
/// So the count is enforced here rather than asked for in prose. Anything not on the
/// allowlist below fails, in the C#-written manifests the rule already covered AND in the
/// checked-in fixture manifests it did not.
///
/// Matching is on the JSON PROPERTY (<c>"application"</c> followed by a colon), never the
/// bare word: several files quote <c>"application"</c> in a comment explaining that they
/// deliberately do NOT declare it, and flagging those would train readers to ignore this
/// test.
/// </summary>
public sealed class BaseAppFloorFixtureGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestsDir => Path.Combine(RepoRoot, "AlRunner.Tests");

    /// <summary>An allowlist key: the path relative to AlRunner.Tests/, '/' separated.</summary>
    private static string Rel(string path) =>
        Path.GetRelativePath(TestsDir, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Every <c>.cs</c> source in the project, at ANY depth, minus build output (the SDK
    /// generates sources under <c>obj/</c>). #3000: the source scan below enumerated
    /// <c>SearchOption.TopDirectoryOnly</c> while the fixture scan in the same class already
    /// walked <c>Fixtures/</c> recursively — so a .cs file below the top level was invisible
    /// to one half of a guard whose whole value is that it fails automatically.
    /// </summary>
    private static IReadOnlyList<string> TestSourcePaths()
    {
        var paths = Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Rel(p).Split('/').Any(seg => seg is "bin" or "obj"))
            .ToList();

        // #3021 — non-vacuity. Nothing else in this class notices an empty scan: the
        // stale-entry fact below probes File.Exists on the allowlist keys directly, so it stays
        // green while both scanning facts pass having read no file at all.
        Assert.True(paths.Count > 0,
            $"expected .cs sources under {TestsDir}, found none — the guard is not looking at "
            + "anything, so a manifest written with the Base Application floor would pass unseen.");

        return paths;
    }

    /// <summary>
    /// Every checked-in fixture manifest, at any depth under <c>Fixtures/</c>. Same non-vacuity
    /// obligation as <see cref="TestSourcePaths"/>, and for the same reason (#3021).
    /// </summary>
    private static IReadOnlyList<string> FixtureManifestPaths()
    {
        var fixtures = Path.Combine(TestsDir, "Fixtures");
        var paths = Directory.EnumerateFiles(fixtures, "app.json", SearchOption.AllDirectories).ToList();

        Assert.True(paths.Count > 0,
            $"expected app.json fixtures under {fixtures}, found none — "
            + "the guard is not looking at anything.");

        return paths;
    }

    // A JSON "application" property, in a .json file or embedded in a C# string —
    // including the escaped \"application\" form used in concatenated manifests.
    private static readonly Regex ApplicationProperty = new(
        @"\\?""application\\?""\s*:", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="path"/> DECLARES the Base Application floor.
    /// </summary>
    internal static bool DeclaresBaseApplicationFloor(string path) =>
        DeclaresBaseApplicationFloor(path, File.ReadAllText(path));

    /// <summary>
    /// RED placeholder (#3064): still the raw-text scan, so a comment quoting the property
    /// counts as declaring it. Replaced in the next commit.
    /// </summary>
    internal static bool DeclaresBaseApplicationFloor(string fileName, string text) =>
        ApplicationProperty.IsMatch(text);

    /// <summary>
    /// Checked-in fixture manifests permitted to declare the floor, with the reason. Each
    /// one has the FLOOR ITSELF as its subject: remove it and the fixture stops testing
    /// anything. Paths are relative to AlRunner.Tests/, with '/' separators.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedFixtures = new()
    {
        ["Fixtures/BcFloorSkip/healthy-suite/app.json"] =
            "BcVersionFloorSkipTests — a floor every supported BC satisfies; the fixture IS the floor",
        ["Fixtures/BcFloorSkip/future-suite/app.json"] =
            "BcVersionFloorSkipTests — a floor no artifact can satisfy; the fixture IS the floor",
        ["Fixtures/CrossMajorNote/app.json"] =
            "CrossMajorNoteTests (#2210) — declares a BC major no shipped engine matches",
        ["Fixtures/SubscriberScanAudit/app.json"] =
            "EventSubscriberScanEquivalenceTests — needs the real Base App + System App " +
            "closure loaded so the metadata-vs-reflection subscriber scan has >3000 real " +
            "[NavEventSubscriber] methods to compare; the floor IS the subject",
    };

    /// <summary>
    /// C# test sources permitted to write the floor into a manifest they generate. Three
    /// are debt tracked in #2364, not permission; two are legitimate. Keep the reasons —
    /// the rule was ignored once already because it read as advice. Paths are relative to
    /// AlRunner.Tests/ with '/' separators, exactly like <see cref="AllowedFixtures"/>; for a
    /// top-level file that is just the file name, which is why widening the scan (#3000) left
    /// every entry below unchanged.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSources = new()
    {
        ["InstallBaselineDiskCacheTests.cs"] =
            "#2364 debt — #1867 install-baseline caching needs a closure whose install writes rows",
        ["InstallSeedDepCompanyCacheTests.cs"] =
            "#2364 debt — same install-baseline closure requirement",
        ["MissingTestDataDiagnosisTests.cs"] =
            "#2364 debt — resolves 'Source Code Setup' (table 242) against real metadata",
        ["PlaceholderFloorProvisioningTests.cs"] =
            "legitimate — the placeholder 1.0.0.0 floor IS the subject",
        ["ProvisionExplicitModesTests.cs"] =
            "legitimate — asserts provisioning against a bundle declaring an older major",
    };

    [Fact]
    public void NoFixtureManifest_DeclaresTheBaseApplicationFloor_ExceptTheAllowlistedFour()
    {
        var offenders = new List<string>();
        foreach (var path in FixtureManifestPaths())
        {
            if (!DeclaresBaseApplicationFloor(path)) continue;
            var rel = Rel(path);
            if (!AllowedFixtures.ContainsKey(rel)) offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "These fixture app.json files declare \"application\" (the Base Application floor) " +
            "without being on the allowlist in this file:\n  " + string.Join("\n  ", offenders) +
            "\n\nSee .claude/rules/no-base-app-in-csharp-tests.md. Declaring it makes every runner " +
            "subprocess spawned against the bundle resolve and load the platform closure. Drop the " +
            "property, or add the fixture here WITH the reason the floor is its subject.");
    }

    [Fact]
    public void NoTestSource_WritesTheBaseApplicationFloor_ExceptTheAllowlistedFive()
    {
        var offenders = new List<string>();
        foreach (var path in TestSourcePaths())
        {
            if (!DeclaresBaseApplicationFloor(path)) continue;
            var rel = Rel(path);
            if (!AllowedSources.ContainsKey(rel)) offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "These test sources write \"application\" into a manifest without being on the " +
            "allowlist in this file:\n  " + string.Join("\n  ", offenders) +
            "\n\nSee .claude/rules/no-base-app-in-csharp-tests.md, and #2364 for the three " +
            "outstanding violations that are debt rather than precedent.");
    }

    /// <summary>
    /// The negative direction: an allowlist entry naming something that no longer declares
    /// the floor is stale, and a stale entry silently re-permits the next one that does.
    /// This is what turns the lists above into a shrinking budget rather than decoration.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_StillDeclaresTheFloor_SoTheListCannotGoStale()
    {
        var stale = new List<string>();

        foreach (var (rel, why) in AllowedFixtures)
        {
            var path = Path.Combine(TestsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !DeclaresBaseApplicationFloor(path))
                stale.Add($"{rel} ({why})");
        }

        foreach (var (rel, why) in AllowedSources)
        {
            var path = Path.Combine(TestsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !DeclaresBaseApplicationFloor(path))
                stale.Add($"{rel} ({why})");
        }

        Assert.True(stale.Count == 0,
            "These allowlist entries no longer declare \"application\" (or no longer exist). " +
            "Delete them — a stale entry silently permits the next fixture that takes the name:\n  "
            + string.Join("\n  ", stale));
    }

    // ── #3064: what counts as DECLARING the floor, versus merely spelling it ──────────────
    //
    // These facts drive DeclaresBaseApplicationFloor directly, on synthetic content, because
    // the two scanning facts above can only ever assert about the sources that happen to be
    // checked in today — they cannot show what the matcher does with a shape nobody has
    // written yet.
    //
    // The guard scans every .cs file in this project, THIS ONE INCLUDED. So the fixtures below
    // must never spell the manifest key next to a colon in this source; they compose it from
    // Key at runtime instead. Spelling it here would make this file an offender of the rule it
    // enforces, and the only ways out would be adding it to AllowedSources — recording a
    // violation that does not exist, which is exactly the degradation #3064 warns about — or
    // deleting the tests.

    private const string Key = "application";

    /// <summary>
    /// Expands a fixture's placeholders: <c>PROP</c> becomes the quoted manifest key,
    /// <c>EPROP</c> the backslash-escaped form a manifest embedded in a C# string literal
    /// uses. EPROP is substituted first, because PROP is a substring of it.
    /// </summary>
    private static string Sub(string template) => template
        .Replace("EPROP", "\\\"" + Key + "\\\"")
        .Replace("PROP", "\"" + Key + "\"");

    /// <summary>
    /// Anchors the placeholder trick against reality: <see cref="Sub"/> must produce the exact
    /// spelling a genuine allowlisted manifest uses, and that manifest must still read as a
    /// declaration. Without this, every fixture below could agree with a matcher that is wrong
    /// about what a real app.json looks like.
    /// </summary>
    [Fact]
    public void ThePlaceholderExpansion_MatchesARealAllowlistedManifest()
    {
        var real = Path.Combine(TestsDir, "Fixtures", "SubscriberScanAudit", "app.json");
        Assert.True(File.Exists(real), $"{real} not found — was the fixture renamed?");

        Assert.Contains(Sub("PROP"), File.ReadAllText(real), StringComparison.Ordinal);
        Assert.True(DeclaresBaseApplicationFloor(real));
    }

    /// <summary>
    /// #3064, the reported defect. A source that quotes the property while explaining that it
    /// deliberately does NOT declare the floor is not a violation. The raw-text scan could not
    /// tell the two apart, so ServerAppVersionBumpTests.cs (PR #2908) failed the BC 27.5 and
    /// 28.4 legs for a <c>//</c> comment written to document compliance with this very rule,
    /// and the cheapest way back to green was to delete the explanation.
    /// </summary>
    [Fact]
    public void AProseCommentQuotingTheProperty_IsNotADeclaration()
    {
        var src = Sub(""""
            public class Fixture
            {
                // No PROP: "1.0.0.0" here — the Base Application floor is not the subject.
                /* Nor PROP: "27.0.0.0" in a block comment. */
                /// <summary>Nor <c>PROP: "1.0.0.0"</c> in a doc comment.</summary>
                public string Manifest() => "{ \"id\": \"a\", \"platform\": \"1.0.0.0\" }";
            }
            """");

        Assert.False(DeclaresBaseApplicationFloor("Fixture.cs", src),
            "a comment quoting the property declares no floor: nothing in this source reaches a "
            + "manifest, so no runner subprocess loads the platform closure because of it.");
    }

    /// <summary>
    /// The control that stops the fix above from being a weakening: the plain shape every
    /// allowlisted source actually uses — a manifest embedded in a C# string literal with the
    /// quotes escaped — must still read as a declaration. This one is green before AND after,
    /// which is what makes the RED case believable rather than a matcher that returns false.
    /// </summary>
    [Fact]
    public void AManifestInAStringLiteral_IsADeclaration()
    {
        var src = Sub(""""
            public class Fixture
            {
                public string Manifest() => "{ \"id\": \"a\", EPROP: \"1.0.0.0\" }";
            }
            """");

        Assert.True(DeclaresBaseApplicationFloor("Fixture.cs", src));
    }

    /// <summary>
    /// ProvisionExplicitModesTests.cs's shape: the property is written in one literal and the
    /// version concatenated on from a variable, so the key and its value never share a token.
    /// </summary>
    [Fact]
    public void AManifestConcatenatedAcrossLiterals_IsADeclaration()
    {
        var src = Sub(""""
            public class Fixture
            {
                public string Manifest(string major) => "{ EPROP: \"" + major + ".0.0.0\" }";
            }
            """");

        Assert.True(DeclaresBaseApplicationFloor("Fixture.cs", src));
    }

    /// <summary>A raw string literal carries no escapes, so the key is spelled plainly.</summary>
    [Fact]
    public void AManifestInARawStringLiteral_IsADeclaration()
    {
        var src = Sub(""""
            public class Fixture
            {
                public string Manifest() => """
                    { "id": "a", PROP: "1.0.0.0" }
                    """;
            }
            """");

        Assert.True(DeclaresBaseApplicationFloor("Fixture.cs", src));
    }

    /// <summary>And an interpolated one, where the key sits in interpolated-text tokens.</summary>
    [Fact]
    public void AManifestBuiltByInterpolation_IsADeclaration()
    {
        var src = Sub(""""
            public class Fixture
            {
                public string Manifest(string v) => $"{{ EPROP: \"{v}\" }}";
            }
            """");

        Assert.True(DeclaresBaseApplicationFloor("Fixture.cs", src));
    }

    /// <summary>A source that writes only the platform floor declares no Base App floor.</summary>
    [Fact]
    public void ASourceWritingOnlyThePlatformFloor_IsNotADeclaration()
    {
        var src = """"
            public class Fixture
            {
                public string Manifest() => "{ \"id\": \"a\", \"platform\": \"1.0.0.0\" }";
            }
            """";

        Assert.False(DeclaresBaseApplicationFloor("Fixture.cs", src));
    }

    /// <summary>The checked-in-fixture half: a manifest that declares the floor at the root.</summary>
    [Fact]
    public void AFixtureManifestDeclaringTheFloor_IsADeclaration() =>
        Assert.True(DeclaresBaseApplicationFloor("app.json",
            Sub("""{ "id": "a", "platform": "1.0.0.0", PROP: "1.0.0.0" }""")));

    /// <summary>And one that does not.</summary>
    [Fact]
    public void AFixtureManifestWithoutTheFloor_IsNotADeclaration() =>
        Assert.False(DeclaresBaseApplicationFloor("app.json",
            """{ "id": "a", "platform": "1.0.0.0", "dependencies": [] }"""));

    /// <summary>
    /// Nested, not root: all three readers of this property —
    /// <c>Dependencies.ReadDependencies</c>, <c>InProcessAppPackager</c> and
    /// <c>Provisioning</c> — call <c>root.TryGetProperty("application", …)</c>, so a key of
    /// that name inside a <c>dependencies[]</c> entry injects no implicit Microsoft/Application
    /// root and costs nothing. The raw-text scan called it a violation.
    /// </summary>
    [Fact]
    public void TheKeyNestedInsideAnotherObject_IsNotTheTopLevelFloor() =>
        Assert.False(DeclaresBaseApplicationFloor("app.json",
            Sub("""{ "id": "a", "dependencies": [ { "name": "X", PROP: "1.0.0.0" } ] }""")));

    /// <summary>
    /// Same three readers gate on <c>ValueKind == JsonValueKind.String</c> and a non-blank
    /// value, so a null floor synthesises no dependency either.
    /// </summary>
    [Fact]
    public void ANullFloor_IsNotADeclaration() =>
        Assert.False(DeclaresBaseApplicationFloor("app.json", Sub("""{ "id": "a", PROP: null }""")));

    /// <summary>
    /// A manifest that will not parse must not read as clean. The precision this fix buys comes
    /// from understanding the file; on input the matcher does not understand, it falls back to
    /// the raw scan and over-reports, because a guard answering "no violation" about content it
    /// could not read is the exact failure mode this class exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnparseableManifest_FallsBackToTheRawScan_RatherThanReadingClean() =>
        Assert.True(DeclaresBaseApplicationFloor("app.json",
            Sub("""{ "id": "a", PROP: "1.0.0.0", }}}""")));
}
