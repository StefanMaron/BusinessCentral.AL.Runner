// CountBaselineTests — unit-level coverage for AlRunner.Infrastructure.CountBaseline
// (#1880), independent of a running BC engine: pure JSON-schema parsing and pure
// comparison logic. The end-to-end proof that this is actually wired into the runner
// (a real dropped/grown run turning a real process exit red/green) lives in
// CountBaselineIntegrationTests, which spawns the real al-runner binary.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CountBaselineManifestSchemaTests : IDisposable
{
    private readonly string _path = TestScratch.FilePath("al-runner-count-baseline-schema", "manifest.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private CountBaselineManifest Load(string json)
    {
        File.WriteAllText(_path, json);
        return CountBaselineManifest.Load(_path);
    }

    private string LoadError(string json) =>
        Assert.Throws<InvalidOperationException>(() => Load(json)).Message;

    [Fact]
    public void MissingFile_ThrowsNamingThePath()
    {
        var missing = _path; // never written
        var ex = Assert.Throws<InvalidOperationException>(() => CountBaselineManifest.Load(missing));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void ValidManifest_LoadsTestsAndAppGroupsWithByBcVersionOverride()
    {
        var m = Load("""
        {
          "suites": {
            "runner-extras": {
              "tests": { "default": 116, "byBcVersion": { "27.0": 110, "27.3": 110 } },
              "appGroups": { "default": 23 }
            }
          }
        }
        """);

        Assert.True(m.Suites.ContainsKey("runner-extras"));
        var suite = m.Suites["runner-extras"];
        Assert.NotNull(suite.Tests);
        Assert.Equal(116, suite.Tests!.Resolve(null));
        Assert.Equal(116, suite.Tests.Resolve("28.1"));   // not overridden -> default
        Assert.Equal(110, suite.Tests.Resolve("27.0"));   // overridden
        Assert.Equal(110, suite.Tests.Resolve("27.3"));
        Assert.NotNull(suite.AppGroups);
        Assert.Equal(23, suite.AppGroups!.Resolve("27.0"));  // no override table at all -> default
    }

    [Fact]
    public void InvalidJson_IsRejected()
    {
        Assert.Contains("invalid JSON", LoadError("{ not json"));
    }

    [Fact]
    public void MissingSuitesRoot_IsRejected()
    {
        Assert.Contains("must be an object with a 'suites'", LoadError("""{ "notSuites": {} }"""));
    }

    [Fact]
    public void SuiteWithNeitherMetric_IsRejected()
    {
        Assert.Contains("declares none of 'groups', 'tests', 'appGroups'",
            LoadError("""{ "suites": { "x": {} } }"""));
    }

    [Fact]
    public void MetricWithoutDefault_IsRejected()
    {
        Assert.Contains("'x'.tests.default must be an integer",
            LoadError("""{ "suites": { "x": { "tests": {} } } }"""));
    }

    [Fact]
    public void MetricDefaultNotAnInteger_IsRejected()
    {
        Assert.Contains("'x'.tests.default must be an integer",
            LoadError("""{ "suites": { "x": { "tests": { "default": "116" } } } }"""));
    }

    [Fact]
    public void ByBcVersionEntryNotAnInteger_IsRejected()
    {
        Assert.Contains("byBcVersion.27.0 must be an integer",
            LoadError("""{ "suites": { "x": { "tests": { "default": 1, "byBcVersion": { "27.0": "a" } } } } } """));
    }
}

public sealed class CountBaselineCheckTests
{
    private static CountBaselineManifest ManifestWith(string suitesJson)
    {
        var path = TestScratch.FilePath("al-runner-cbc", "manifest.json");
        try
        {
            File.WriteAllText(path, $$"""{ "suites": {{{suitesJson}}} }""");
            return CountBaselineManifest.Load(path);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static Dictionary<string, SuiteCountActual> Actual(params (string Suite, int Tests, int Groups)[] rows) =>
        rows.ToDictionary(r => r.Suite, r => new SuiteCountActual(r.Tests, r.Groups));

    /// <summary>
    /// The core proving case: actual below the expected count is a DROP, never a growth, and
    /// the finding carries the exact suite/metric/expected/actual — a stub that always
    /// returns "no drops" would fail this immediately.
    /// </summary>
    [Fact]
    public void ActualBelowExpected_IsADrop_WithExactFields()
    {
        var manifest = ManifestWith("""
        "al-language": { "tests": { "default": 2073 } }
        """);
        var actual = Actual(("al-language", Tests: 2070, Groups: 1));

        var (drops, growths) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");

        var drop = Assert.Single(drops);
        Assert.Equal("al-language", drop.Suite);
        Assert.Equal("tests", drop.Metric);
        Assert.Equal(2073, drop.Expected);
        Assert.Equal(2070, drop.Actual);
        Assert.Empty(growths);
    }

    /// <summary>Negative: actual exactly at the expected count is neither a drop nor a growth.</summary>
    [Fact]
    public void ActualEqualsExpected_IsNeitherDropNorGrowth()
    {
        var manifest = ManifestWith("""
        "al-language": { "tests": { "default": 2073 } }
        """);
        var actual = Actual(("al-language", Tests: 2073, Groups: 1));

        var (drops, growths) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");

        Assert.Empty(drops);
        Assert.Empty(growths);
    }

    /// <summary>Positive: actual above the expected count is a growth, and specifically NOT a drop.</summary>
    [Fact]
    public void ActualAboveExpected_IsAGrowth_NeverADrop()
    {
        var manifest = ManifestWith("""
        "al-language": { "tests": { "default": 2073 } }
        """);
        var actual = Actual(("al-language", Tests: 2100, Groups: 1));

        var (drops, growths) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");

        Assert.Empty(drops);
        var growth = Assert.Single(growths);
        Assert.Equal(2073, growth.Expected);
        Assert.Equal(2100, growth.Actual);
    }

    /// <summary>
    /// Design constraint #4: per-BC-version counts legitimately differ, and must be
    /// resolved EXPLICITLY per version — not by taking a single min() across every
    /// version. The SAME actual count (110) is a drop against 28.1's expected count (116) but
    /// a clean match against 27.0's expected count (110), in the SAME manifest.
    /// </summary>
    [Fact]
    public void ByBcVersion_DifferentVersionsGetDifferentExpectedCounts_NotAGlobalMinimum()
    {
        var manifest = ManifestWith("""
        "runner-extras": { "tests": { "default": 116, "byBcVersion": { "27.0": 110 } } }
        """);
        var actual = Actual(("runner-extras", Tests: 110, Groups: 23));

        var (dropsOn28_1, _) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");
        var (dropsOn27_0, _) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "27.0");

        Assert.Single(dropsOn28_1);              // 110 < 116 (default expected count) -> drop
        Assert.Equal(116, dropsOn28_1[0].Expected);
        Assert.Empty(dropsOn27_0);                // 110 == 110 (27.0 override) -> no drop
    }

    /// <summary>
    /// The app-group metric is independent of the tests metric: a suite can drop on
    /// one and not the other, and both are reported.
    /// </summary>
    [Fact]
    public void AppGroupsAndTests_AreIndependentMetrics_BothReported()
    {
        var manifest = ManifestWith("""
        "runner-extras": {
          "tests": { "default": 116 },
          "appGroups": { "default": 23 }
        }
        """);
        // tests grew, app groups dropped — both must surface, in their own bucket.
        var actual = Actual(("runner-extras", Tests: 120, Groups: 20));

        var (drops, growths) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");

        var drop = Assert.Single(drops);
        Assert.Equal("appGroups", drop.Metric);
        Assert.Equal(23, drop.Expected);
        Assert.Equal(20, drop.Actual);

        var growth = Assert.Single(growths);
        Assert.Equal("tests", growth.Metric);
    }

    /// <summary>A suite the manifest does not mention imposes no expectation at all.</summary>
    [Fact]
    public void SuiteNotInManifest_IsIgnored()
    {
        var manifest = ManifestWith("""
        "al-language": { "tests": { "default": 2073 } }
        """);
        var actual = Actual(("some-other-suite", Tests: 0, Groups: 0));

        var (drops, growths) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");

        Assert.Empty(drops);
        Assert.Empty(growths);
    }

    /// <summary>A suite the manifest mentions but this run never touched is silently skipped, not a phantom drop.</summary>
    [Fact]
    public void SuiteInManifestButNotInThisRun_IsIgnored()
    {
        var manifest = ManifestWith("""
        "al-language": { "tests": { "default": 2073 } },
        "runner-extras": { "tests": { "default": 116 } }
        """);
        var actual = Actual(("al-language", Tests: 2073, Groups: 1));   // runner-extras absent

        var (drops, growths) = CountBaselineCheck.Evaluate(manifest, actual, bcVersionKey: "28.1");

        Assert.Empty(drops);
        Assert.Empty(growths);
    }
}

/// <summary>
/// The per-app-group form (#2485). Same guard, different spelling: the expected test count
/// and app-group count are summed from one checked-in line per app group, so two PRs adding
/// different groups no longer edit the same integer. These tests pin that the derivation is
/// exact and version-aware — an implementation that ignored <c>absentOn</c>, or that took a
/// group count instead of a test sum, fails every one of them.
/// </summary>
public sealed class CountBaselineGroupsFormTests
{
    private static CountBaselineManifest Load(string json)
    {
        var path = TestScratch.FilePath("al-runner-cb-groups", "manifest.json");
        try { File.WriteAllText(path, json); return CountBaselineManifest.Load(path); }
        finally { try { File.Delete(path); } catch { } }
    }

    private static string LoadError(string json) =>
        Assert.Throws<InvalidOperationException>(() => Load(json)).Message;

    private const string ThreeGroups = """
    {
      "suites": {
        "runner-extras": {
          "groups": {
            "date-virtual-table-window": { "tests": 3 },
            "dep-only-fixture": { "tests": 0 },
            "microsoft-test-library": { "tests": 3, "absentOn": ["27.0", "27.3"] }
          }
        }
      }
    }
    """;

    [Fact]
    public void TestsAndAppGroups_AreSummedFromTheGroupEntries()
    {
        var suite = Load(ThreeGroups).Suites["runner-extras"];

        // 3 + 0 + 3 across three groups, on a version nothing is absent from.
        Assert.Equal(6, suite.Tests!.Resolve("28.4"));
        Assert.Equal(3, suite.AppGroups!.Resolve("28.4"));
        Assert.Equal(6, suite.Tests.Resolve(null));
        Assert.Equal(3, suite.AppGroups.Resolve(null));
    }

    [Fact]
    public void AbsentOn_RemovesBothTheGroupAndItsTests_OnlyOnThatVersion()
    {
        var suite = Load(ThreeGroups).Suites["runner-extras"];

        Assert.Equal(3, suite.Tests!.Resolve("27.0"));       // 6 - the absent group's 3
        Assert.Equal(2, suite.AppGroups!.Resolve("27.0"));   // 3 - the absent group
        Assert.Equal(3, suite.Tests.Resolve("27.3"));
        Assert.Equal(2, suite.AppGroups.Resolve("27.3"));
        Assert.Equal(6, suite.Tests.Resolve("27.5"));        // NOT listed -> present
        Assert.Equal(3, suite.AppGroups.Resolve("27.5"));
    }

    /// <summary>
    /// The guard's whole point, through the new form: a suite that stops being discovered is
    /// a DROP, and one test more than declared is a GROWTH. Both fail.
    /// </summary>
    [Fact]
    public void DerivedCounts_StillFailInBothDirections()
    {
        var manifest = Load(ThreeGroups);

        var (drops, growths) = CountBaselineCheck.Evaluate(
            manifest,
            new Dictionary<string, SuiteCountActual> { ["runner-extras"] = new(3, 2) },
            "28.4");
        Assert.Empty(growths);
        Assert.Equal(2, drops.Count);
        Assert.Contains(drops, d => d.Metric == "tests" && d is { Expected: 6, Actual: 3 });
        Assert.Contains(drops, d => d.Metric == "appGroups" && d is { Expected: 3, Actual: 2 });

        var (drops2, growths2) = CountBaselineCheck.Evaluate(
            manifest,
            new Dictionary<string, SuiteCountActual> { ["runner-extras"] = new(7, 3) },
            "28.4");
        Assert.Empty(drops2);
        Assert.Single(growths2);
        Assert.Equal("tests", growths2[0].Metric);
        Assert.Equal(6, growths2[0].Expected);
        Assert.Equal(7, growths2[0].Actual);
    }

    [Fact]
    public void GroupsAlongsideFlatCounts_IsRejectedAsTwoSourcesOfTruth()
    {
        Assert.Contains("declares 'groups' AND 'tests'/'appGroups'",
            LoadError("""
            { "suites": { "x": { "groups": { "g": { "tests": 1 } }, "tests": { "default": 1 } } } }
            """));
    }

    [Fact]
    public void GroupWithoutATestCount_IsRejected()
    {
        Assert.Contains("'x'.groups.g.tests must be an integer",
            LoadError("""{ "suites": { "x": { "groups": { "g": {} } } } }"""));
    }

    [Fact]
    public void GroupTestCountNotAnInteger_IsRejected()
    {
        Assert.Contains("'x'.groups.g.tests must be an integer",
            LoadError("""{ "suites": { "x": { "groups": { "g": { "tests": "2" } } } } }"""));
    }

    [Fact]
    public void AbsentOnNotAnArrayOfStrings_IsRejected()
    {
        Assert.Contains("absentOn must be an array",
            LoadError("""{ "suites": { "x": { "groups": { "g": { "tests": 1, "absentOn": "27.0" } } } } }"""));
        Assert.Contains("absentOn entries must be",
            LoadError("""{ "suites": { "x": { "groups": { "g": { "tests": 1, "absentOn": [27] } } } } }"""));
    }

    [Fact]
    public void EmptyGroupsObject_IsRejected()
    {
        Assert.Contains("groups is empty", LoadError("""{ "suites": { "x": { "groups": {} } } }"""));
    }

    [Fact]
    public void GroupsNotAnObject_IsRejected()
    {
        Assert.Contains("groups must be an object", LoadError("""{ "suites": { "x": { "groups": [] } } }"""));
    }

    /// <summary>
    /// The checked-in file must load and answer the numbers the Test Matrix has been
    /// measuring — 260 tests over 50 app groups on 28.x, 249 over 45 on the 27.x legs, where
    /// five app groups declaring platform/application 28.0.0.0 do not run. Asserted as the
    /// per-version DIFFERENCE, not as literal totals, so this test does not become a second
    /// place every count-changing PR has to edit.
    /// </summary>
    [Fact]
    public void CheckedInBaseline_LoadsAndDropsExactlyTheBc28OnlyGroupsOn27x()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var manifest = CountBaselineManifest.Load(Path.Combine(
            repoRoot, "tests", "expectations", "count-baseline", "test-count-baseline.json"));

        var extras = manifest.Suites["runner-extras"];
        var on28 = extras.Tests!.Resolve("28.4");
        var on27 = extras.Tests.Resolve("27.0");
        var groups28 = extras.AppGroups!.Resolve("28.4");
        var groups27 = extras.AppGroups.Resolve("27.0");

        Assert.Equal(groups28 - 5, groups27);
        Assert.Equal(on28 - 11, on27);
        Assert.Equal(extras.Tests.Resolve("27.3"), on27);
        Assert.Equal(extras.Tests.Resolve("27.5"), on27);
        Assert.Equal(on28, extras.Tests.Resolve("28.0"));

        // al-language stays on the flat form on purpose (see this directory's README).
        Assert.NotNull(manifest.Suites["al-language"].Tests);
        Assert.Equal(manifest.Suites["al-language"].Tests!.Resolve("28.4"),
                     manifest.Suites["al-language"].Tests!.Resolve("27.0"));
    }
}
