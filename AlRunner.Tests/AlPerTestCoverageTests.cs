using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2135 — per-test statement attribution: "which tests executed which code", so a
/// mutation-testing harness can narrow which tests could possibly kill a given
/// mutant instead of running every mutant against every test (the issue's own
/// measured cost: 345 mutants x 61 tests, ~5 hours, on one reported fixture).
///
/// `perTestCoverage:true` is a SEPARATE opt-in from `coverage:true` (#2042) — it
/// groups the SAME per-statement hit data BY TEST instead of summing it over the
/// whole run. This class proves: (1) a statement only one test executed is
/// attributed to THAT test and no other; (2) a statement BOTH tests executed is
/// attributed to both, each with its own hit count; (3) the opt-in is independent
/// of `coverage:true` — either flag works without the other; (4) omitting the flag
/// leaves `perTestCoverage` entirely absent, never an empty/stale array.
///
/// Ghost-test guard: every assertion names a SPECIFIC test key, statement position,
/// or hit count — never just "perTestCoverage present". An implementation that
/// dumps the AGGREGATE table under every test key (i.e. doesn't actually
/// discriminate by test) fails <see cref="StatementOnlyOneTestExecuted_AttributedToThatTestAlone"/>
/// specifically, because the never-executed test's entry would wrongly include it.
///
/// Spawns the real runner in --server mode; needs the BC artifact cache — reports
/// Skipped (not Passed) when absent, via TestArtifacts.
/// </summary>
public class AlPerTestCoverageTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public AlPerTestCoverageTests(SharedCliServer fixture) => _fixture = fixture;

    // Mirrors AlStatementTableTests.MakeRunTestsBundle's disk-bundle shape — own
    // AppId/idRange so this class never collides with another suite's compiled
    // module cache.
    private static string MakeRunTestsBundle(string sourceFile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-per-test-coverage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "9f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b",
          "name": "Per-Test Coverage Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60200, "to": 60209 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), sourceFile);
        return dir;
    }

    // Two [Test] procedures in one codeunit: OnlyA touches ONLY the "A"-branch
    // statement; OnlyB touches ONLY the "B"-branch statement; both touch the shared
    // "Shared := " statement before branching. Line numbers are pinned in comments
    // so the assertions below read as facts about specific AL source, not guesses.
    private const string TwoTestCodeunit = """
    codeunit 60200 "PerTest Cov SX"
    {
        Subtype = Test;

        [Test]
        procedure OnlyA()
        var
            Shared: Integer;
            OnlyAValue: Integer;
        begin
            Shared := 1;
            OnlyAValue := 10;
        end;

        [Test]
        procedure OnlyB()
        var
            Shared: Integer;
            OnlyBValue: Integer;
        begin
            Shared := 1;
            OnlyBValue := 20;
        end;
    }
    """;

    private static string RunTestsRequest(string bundle, bool coverage = false, bool perTestCoverage = false,
        string? testIsolation = null)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            coverage,
            perTestCoverage,
            testIsolation,
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
        });

    // The core positive claim: OnlyAValue's assignment statement (line 12, "OnlyAValue
    // := 10;") is executed ONLY by OnlyA — it must appear under OnlyA's per-test entry
    // and must NOT appear under OnlyB's, even though both tests share the SAME
    // codeunit/scope Type and the SAME "Shared := 1;" statement.
    [SkippableFact]
    public async Task StatementOnlyOneTestExecuted_AttributedToThatTestAlone()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeRunTestsBundle(TwoTestCodeunit);
        var server = await _fixture.GetAsync();
        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, perTestCoverage: true));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        Assert.True(summary.TryGetProperty("perTestCoverage", out var perTest),
            $"expected perTestCoverage on the summary: {string.Join(" | ", lines)}");
        var entries = perTest.EnumerateArray().ToList();

        var onlyAKey = FindTestKey(entries, "OnlyA");
        var onlyBKey = FindTestKey(entries, "OnlyB");

        var onlyAStatements = StatementsFor(entries, onlyAKey);
        var onlyBStatements = StatementsFor(entries, onlyBKey);

        // OnlyA's own statement (line 12, "OnlyAValue := 10;") is present under OnlyA...
        Assert.Contains(onlyAStatements, s => s.GetProperty("line").GetInt32() == 12);
        // ...and ABSENT under OnlyB. This is the fact a "just dump the aggregate
        // table under every test" fake implementation cannot satisfy.
        Assert.DoesNotContain(onlyBStatements, s => s.GetProperty("line").GetInt32() == 12);

        // Symmetric negative: OnlyB's own statement (line 22, "OnlyBValue := 20;") is
        // present under OnlyB, absent under OnlyA.
        Assert.Contains(onlyBStatements, s => s.GetProperty("line").GetInt32() == 22);
        Assert.DoesNotContain(onlyAStatements, s => s.GetProperty("line").GetInt32() == 22);
    }

    // The shared-statement claim: "Shared := 1;" (line 11 in OnlyA, line 21 in
    // OnlyB — two SEPARATE statements at the AL-source level, each executed exactly
    // once by its own test) each appear under their OWN test with hits:1 — proving
    // per-test hit counts are independent, not aliased or summed across tests.
    [SkippableFact]
    public async Task SharedShapeStatement_EachTestReportsItsOwnHitCountIndependently()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeRunTestsBundle(TwoTestCodeunit);
        var server = await _fixture.GetAsync();
        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, perTestCoverage: true));
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        var entries = summary.GetProperty("perTestCoverage").EnumerateArray().ToList();

        var onlyAStatements = StatementsFor(entries, FindTestKey(entries, "OnlyA"));
        var onlyBStatements = StatementsFor(entries, FindTestKey(entries, "OnlyB"));

        var aShared = Assert.Single(onlyAStatements, s => s.GetProperty("line").GetInt32() == 11);
        Assert.Equal(1, aShared.GetProperty("hits").GetInt32());

        var bShared = Assert.Single(onlyBStatements, s => s.GetProperty("line").GetInt32() == 21);
        Assert.Equal(1, bShared.GetProperty("hits").GetInt32());
    }

    // #2144 landed AFTER this issue's branch was cut: under `testIsolation:"test"`,
    // every [Test] now runs on a BRAND NEW codeunit instance (TestExecutor.Run's
    // `perTestInstance` branch), not the one shared instance Codeunit isolation
    // reuses. The per-test coverage key is "{Codeunit}.{Method}" — the .NET TYPE
    // name plus the AL method name, never the instance — so a fresh instance per
    // test must not change attribution. Repeats the core positive/negative claim
    // from StatementOnlyOneTestExecuted_AttributedToThatTestAlone under Test
    // isolation specifically, to prove the two features compose correctly rather
    // than merely asserting they should by reasoning about the key format.
    [SkippableFact]
    public async Task StatementAttribution_UnaffectedByFreshInstancePerTestUnderTestIsolation()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeRunTestsBundle(TwoTestCodeunit);
        var server = await _fixture.GetAsync();
        var lines = await server.SendRequestStreamingAsync(
            RunTestsRequest(bundle, perTestCoverage: true, testIsolation: "test"));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        Assert.True(summary.TryGetProperty("perTestCoverage", out var perTest),
            $"expected perTestCoverage on the summary: {string.Join(" | ", lines)}");
        var entries = perTest.EnumerateArray().ToList();

        var onlyAStatements = StatementsFor(entries, FindTestKey(entries, "OnlyA"));
        var onlyBStatements = StatementsFor(entries, FindTestKey(entries, "OnlyB"));

        // Same core fact as StatementOnlyOneTestExecuted_AttributedToThatTestAlone:
        // OnlyA's own statement (line 12) present under OnlyA, absent under OnlyB,
        // and symmetrically for OnlyB's own statement (line 22) — now proven with
        // each test running on its OWN codeunit instance rather than a shared one.
        Assert.Contains(onlyAStatements, s => s.GetProperty("line").GetInt32() == 12);
        Assert.DoesNotContain(onlyBStatements, s => s.GetProperty("line").GetInt32() == 12);
        Assert.Contains(onlyBStatements, s => s.GetProperty("line").GetInt32() == 22);
        Assert.DoesNotContain(onlyAStatements, s => s.GetProperty("line").GetInt32() == 22);
    }

    // Independence claim: perTestCoverage:true works WITHOUT coverage:true — the
    // two opt-ins are priced (and gated) separately, per the issue's own design
    // question. `coverage` must stay absent while `perTestCoverage` is present.
    [SkippableFact]
    public async Task PerTestCoverageAlone_WorksWithoutAggregateCoverage()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeRunTestsBundle(TwoTestCodeunit);
        var server = await _fixture.GetAsync();
        var lines = await server.SendRequestStreamingAsync(
            RunTestsRequest(bundle, coverage: false, perTestCoverage: true));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        Assert.False(summary.TryGetProperty("coverage", out _),
            $"coverage must stay absent when only perTestCoverage was requested: {string.Join(" | ", lines)}");
        Assert.True(summary.TryGetProperty("perTestCoverage", out var perTest),
            $"expected perTestCoverage: {string.Join(" | ", lines)}");
        Assert.True(perTest.GetArrayLength() >= 2);
    }

    // Negative direction: perTestCoverage omitted (false by default) must leave the
    // field ABSENT — not an empty array, not present with stale data from a prior
    // request on the same warm server — proving the flag actually gates the
    // feature, same convention `coverage`/`captureValues` already use.
    [SkippableFact]
    public async Task PerTestCoverage_Omitted_NoPerTestCoverageField()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeRunTestsBundle(TwoTestCodeunit);
        var server = await _fixture.GetAsync();
        // Prime the server with a PRIOR perTestCoverage:true request so a
        // process-global flag left on by accident would leak into the next one.
        await server.SendRequestStreamingAsync(RunTestsRequest(bundle, perTestCoverage: true));

        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle));
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.False(summary.TryGetProperty("perTestCoverage", out _),
            $"perTestCoverage must be absent when perTestCoverage:true wasn't requested: {string.Join(" | ", lines)}");
    }

    private static string FindTestKey(List<JsonElement> entries, string methodNameSuffix)
    {
        var match = entries.Single(e => e.GetProperty("test").GetString()!.EndsWith("." + methodNameSuffix, StringComparison.Ordinal));
        return match.GetProperty("test").GetString()!;
    }

    private static List<JsonElement> StatementsFor(List<JsonElement> entries, string testKey)
    {
        var entry = entries.Single(e => e.GetProperty("test").GetString() == testKey);
        return entry.GetProperty("coverage").EnumerateArray()
            .SelectMany(f => f.GetProperty("statements").EnumerateArray())
            .ToList();
    }
}
