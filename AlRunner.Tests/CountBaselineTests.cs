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
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "al-runner-count-baseline-schema-" + Guid.NewGuid().ToString("N") + ".json");

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
        Assert.Contains("declares neither 'tests' nor 'appGroups'",
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
        var path = Path.Combine(Path.GetTempPath(), "al-runner-cbc-" + Guid.NewGuid().ToString("N") + ".json");
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
