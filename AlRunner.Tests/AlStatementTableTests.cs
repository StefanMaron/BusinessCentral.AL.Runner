using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2042 — the statement-position table: per-statement hit counts PLUS the id↔position
/// mapping <c>--capture-values</c> (#2040) needed but never had. Per SShadowS/ALchemist#1
/// (the consumer's own spec, quoted in the issue): ALchemist maps a captured value's
/// <c>statementId</c> to an editor line today by treating it as an index into a
/// covered-lines list SORTED BY LINE NUMBER — a heuristic that breaks on multi-statement
/// lines and skipped statements. This class proves the fix: `coverage:true` on
/// `runTests`/`execute` reports one entry per BC-instrumented statement — id, owning AL
/// member name (`scope`), 1-based start/end line+column, and this run's hit count — and
/// that `id` is PROVABLY the same id-space `capturedValues[].statementId` already uses
/// for the same `scope`, not merely documented to be.
///
/// Ghost-test guard: every assertion below names a SPECIFIC id, line, column, or hit
/// count — never just "coverage present". A no-op implementation (empty array) fails
/// every positive fact; an implementation that sums hits by line instead of keeping
/// statements separate fails <see cref="TwoStatementsOnSameLine_ReportSeparately_NotSummed"/>
/// specifically because it would collapse two entries into one.
///
/// Spawns the real runner in --server mode; needs the BC artifact cache — reports
/// Skipped (not Passed) when absent, via TestArtifacts.
/// </summary>
public class AlStatementTableTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public AlStatementTableTests(SharedCliServer fixture) => _fixture = fixture;

    private static string Req(string command, string code, bool coverage, bool captureValues = false)
        => JsonSerializer.Serialize(new
        {
            command,
            coverage,
            captureValues,
            code,
        });

    // Mirrors ServerTests.MakeExecuteBundle's disk-bundle shape (own AppId/idRange so
    // this class never collides with another suite's compiled-module cache).
    private static string MakeRunTestsBundle(string sourceFile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-stmt-table-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "d4e5f607-1829-4a0b-bc2d-3e4f60718293",
          "name": "Statement Table RunTests Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60190, "to": 60199 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), sourceFile);
        return dir;
    }

    // #2042 acceptance criterion 2, the whole point of the issue: capturedValues'
    // statementId and the statement table's id are the SAME id-space for the SAME
    // scope, proven from ONE `execute` call (not asserted separately and assumed to
    // line up). The codeunit is the SAME 4-statement shape ServerTests'
    // Execute_CaptureValues_True_ReportsOneEntryPerStatementExecutionInOrder proves in
    // full — this fact additionally locates Msg's LAST entry's id in the statement table
    // and asserts its POSITION is the literal AL source line "Msg := 'after';" sits on
    // (line 11 below), not just "some line".
    //
    // #2074 UPDATE: capturedValues now carries ONE entry per statement execution, so
    // Counter appears TWICE (41 at statement 0, 42 at statement 2) — its statementId is
    // no longer uniformly the scope's LAST statement, the way the pre-#2074 snapshot
    // reported it. This test now cross-references Msg's LAST entry instead (still
    // statement 3, the scope's true last statement, since "Msg := 'after';" IS the last
    // statement) so the line-11 assertion below is unchanged; Counter's own corrected
    // attribution (statement 2, not 3) is covered directly by ServerTests.
    [SkippableFact]
    public async Task CapturedValueStatementId_MatchesStatementTableScopeAndId()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var code =
            "codeunit 60190 \"Stmt Table Corr SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var\n" +
            "        Counter: Integer;\n" +
            "        Msg: Text;\n" +
            "    begin\n" +
            "        Counter := 41;\n" +
            "        Msg := 'before';\n" +
            "        Counter := 42;\n" +
            "        Msg := 'after';\n" +
            "    end;\n" +
            "}\n";
        var r = await server.SendAsync(Req("execute", code, coverage: true, captureValues: true));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());

        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        var captured = tests[0].GetProperty("capturedValues");
        // Msg is assigned TWICE (statement 1, then statement 3) — take its LAST entry,
        // the "final value" a caller reading only the tail of the series would see.
        var msgEntry = captured.EnumerateArray()
            .Where(e => e.GetProperty("variableName").GetString() == "Msg")
            .Last();
        Assert.Equal("OnRun", msgEntry.GetProperty("scopeName").GetString());
        Assert.Equal("after", msgEntry.GetProperty("value").GetString());
        var capturedStatementId = msgEntry.GetProperty("statementId").GetInt32();
        Assert.Equal(3, capturedStatementId); // 4 statements, 0-based, last one

        Assert.True(d.TryGetProperty("coverage", out var coverage), $"expected coverage on the response: {r}");
        var fileEntry = coverage.EnumerateArray().Single();
        var statements = fileEntry.GetProperty("statements").EnumerateArray().ToList();
        Assert.Equal(4, statements.Count);

        // The core claim: find the statement-table row whose (scope, id) matches
        // capturedValues' (scopeName, statementId) EXACTLY, then assert its position
        // is the real AL source line the captured value came from — not a guess from
        // a sorted covered-lines index (the heuristic ALchemist's own issue reply
        // names as broken).
        var matched = statements.Single(s =>
            s.GetProperty("scope").GetString() == "OnRun" &&
            s.GetProperty("id").GetInt32() == capturedStatementId);
        Assert.Equal(11, matched.GetProperty("line").GetInt32()); // "Msg := 'after';"
        Assert.Equal(1, matched.GetProperty("hits").GetInt32());
        Assert.True(matched.GetProperty("column").GetInt32() > 0);
        Assert.True(matched.GetProperty("endColumn").GetInt32() >= matched.GetProperty("column").GetInt32());
    }

    // #2042 acceptance criterion 3, first half: a statement hit N times reports N —
    // not 1 (vacuous "it ran"), not 0. The loop body ("Counter := Counter + 1;")
    // executes exactly 4 times; the loop-entry statement itself executes exactly once
    // (entering the FOR construct, not its body).
    [SkippableFact]
    public async Task LoopBodyStatement_HitFourTimes_ReportsHitCountFour()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var code =
            "codeunit 60191 \"Stmt Table Loop SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var\n" +
            "        i: Integer;\n" +
            "        Counter: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 4 do\n" +
            "            Counter := Counter + 1;\n" +
            "    end;\n" +
            "}\n";
        var r = await server.SendAsync(Req("execute", code, coverage: true));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.True(d.TryGetProperty("coverage", out var coverage), $"expected coverage on the response: {r}");
        var statements = coverage.EnumerateArray().Single()
            .GetProperty("statements").EnumerateArray().ToList();

        var loopEntry = statements.Single(s => s.GetProperty("line").GetInt32() == 8);
        Assert.Equal(1, loopEntry.GetProperty("hits").GetInt32());

        var loopBody = statements.Single(s => s.GetProperty("line").GetInt32() == 9);
        Assert.Equal(4, loopBody.GetProperty("hits").GetInt32());
    }

    // #2042 acceptance criterion 3, second half: two statements sharing a source
    // line are reported as TWO SEPARATE entries with their OWN hit counts, not one
    // entry with a summed count. "Counter := 100;" and "Counter := 200;" sit on the
    // SAME line (10) but are different statements with different ids/columns — a
    // rollup implementation would report ONE entry for line 10 with hits:2; this
    // fact fails against that shape and only passes against genuinely per-statement
    // tracking.
    [SkippableFact]
    public async Task TwoStatementsOnSameLine_ReportSeparately_NotSummed()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var code =
            "codeunit 60192 \"Stmt Table SameLine SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var\n" +
            "        Counter: Integer;\n" +
            "    begin\n" +
            "        Counter := 100; Counter := 200;\n" +
            "    end;\n" +
            "}\n";
        var r = await server.SendAsync(Req("execute", code, coverage: true));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.True(d.TryGetProperty("coverage", out var coverage), $"expected coverage on the response: {r}");
        var statements = coverage.EnumerateArray().Single()
            .GetProperty("statements").EnumerateArray().ToList();

        var sameLine = statements.Where(s => s.GetProperty("line").GetInt32() == 7).ToList();
        // TWO entries, not one — the ghost-test check a summed-by-line
        // implementation cannot pass: it would produce Count == 1.
        Assert.Equal(2, sameLine.Count);
        Assert.All(sameLine, s => Assert.Equal(1, s.GetProperty("hits").GetInt32()));
        // Distinct ids and distinct (non-overlapping) columns — proves these are
        // really two different statements, not the same one reported twice.
        var ids = sameLine.Select(s => s.GetProperty("id").GetInt32()).Distinct().ToList();
        Assert.Equal(2, ids.Count);
        var columns = sameLine.Select(s => s.GetProperty("column").GetInt32()).OrderBy(c => c).ToList();
        Assert.True(columns[1] > columns[0]);
    }

    // Negative direction: coverage omitted (false by default) must leave `coverage`
    // ABSENT — not an empty array, not present with stale data — proving the flag
    // actually gates the feature (same convention captureValues already uses).
    [SkippableFact]
    public async Task Coverage_Omitted_NoCoverageField()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 60193 \"Stmt Table NoCov SX\" { trigger OnRun() var Counter: Integer; " +
                   "begin Counter := 1; end; }",
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.False(d.TryGetProperty("coverage", out _),
            $"coverage must be absent when coverage:true wasn't requested: {r}");
    }

    // Regression guard for the ghost-generation bug this issue's own investigation
    // surfaced: RunBundleForServer calls Assembly.Load(assemblyBytes) again on EVERY
    // request that isn't a cross-bundle-dedup reuse — including a pure AL-output
    // cache HIT — so a warm server accumulates one Assembly generation PER REQUEST
    // for the SAME bundle (assemblies never unload). A statement-table scan that
    // walked every loaded assembly (rather than the current run's own hit-tracked
    // types) reported the SAME statement once per generation ever loaded: the live
    // one with a real count, plus one phantom-zero ghost per stale generation.
    // Reproduced empirically (2nd+ identical runTests request against one warm
    // server) before AlCoverageTracker.GetHitTrackedTypes() restricted the scan.
    // This sends the SAME runTests request three times on one shared server and
    // asserts every response has EXACTLY the statements the bundle declares — no
    // duplicates, no drift in hit counts across repeats.
    [SkippableFact]
    public async Task Coverage_RepeatedIdenticalRequests_NeverGhostDuplicates()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeRunTestsBundle("""
        codeunit 60194 "Stmt Table Repeat SX"
        {
            Subtype = Test;

            [Test]
            procedure LoopHitsThrice()
            var
                i: Integer;
                Counter: Integer;
            begin
                for i := 1 to 3 do
                    Counter := Counter + 1;
            end;
        }
        """);
        var server = await _fixture.GetAsync();
        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            coverage = true,
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
        });

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var lines = await server.SendRequestStreamingAsync(req);
            var (_, summary) = ProtocolV2Streaming.Split(lines);
            Assert.True(summary.TryGetProperty("coverage", out var coverage),
                $"attempt {attempt}: expected coverage on the summary: {string.Join(" | ", lines)}");
            var statements = coverage.EnumerateArray().Single()
                .GetProperty("statements").EnumerateArray().ToList();

            // Exactly 2 statements (loop entry + loop body) — never 4/6/... from an
            // accumulating ghost generation.
            Assert.Equal(2, statements.Count);
            var ids = statements.Select(s => s.GetProperty("id").GetInt32()).OrderBy(x => x).ToList();
            Assert.Equal(new[] { 0, 1 }, ids);

            var loopBody = statements.Single(s => s.GetProperty("id").GetInt32() == 1);
            Assert.Equal(3, loopBody.GetProperty("hits").GetInt32());
        }
    }
}
