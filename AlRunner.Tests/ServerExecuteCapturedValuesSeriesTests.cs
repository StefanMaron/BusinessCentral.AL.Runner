using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2074 — `--capture-values` used to record ONE end-of-test snapshot of a top-
/// level OnRun scope's AL locals (AlValueCapture.OnExit walked every [NavName] field
/// once, unconditionally, when the scope's method body finished). A local reassigned
/// inside a loop that runs N times collapsed to its FINAL value only — ALchemist's
/// inline loop rendering (SShadowS/ALchemist#1, this issue's own reporter) needs the
/// full per-iteration series, e.g. `myInt = 2 .. 56 (x10)`.
///
/// This class proves the fix end-to-end against a real compiled+executed AL loop (needs
/// the BC artifact cache — see TestArtifacts). The pure diff-engine mechanism tests live
/// in AlValueCaptureSeriesTests (no BC needed).
///
/// Ghost-test guard: every positive assertion names SPECIFIC values in a SPECIFIC order.
/// A no-op fix (still one entry per variable) fails
/// <see cref="Execute_CaptureValues_LoopReassignsVariable_ReportsOneEntryPerIteration"/>
/// outright (it asserts 3 entries with DIFFERENT values sharing ONE statementId); an
/// implementation that always emits every field on every statement (no diffing) fails
/// the same test differently (way more than 3 entries) and would also fail the negative
/// direction below (an untouched local must never surface).
/// </summary>
public class ServerExecuteCapturedValuesSeriesTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerExecuteCapturedValuesSeriesTests(SharedCliServer fixture) => _fixture = fixture;

    // Same loop shape as ServerExecuteMessagesTests' LoopAndFinalMessageCode (#2117) —
    // a `for` loop running 3 times, reassigning `total` to a DIFFERENT cumulative value
    // each iteration (1, 3, 6 — never the same value twice, so DiffAndUpdate's own
    // "unchanged value = no record" behaviour can never be mistaken for the real feature
    // working), followed by one more assignment on a separate, later statement.
    private const string LoopReassignsThenFinalAssignCode =
        "codeunit 60210 \"CV Loop Series SX\"\n" +
        "{\n" +
        "    trigger OnRun()\n" +
        "    var\n" +
        "        i: Integer;\n" +
        "        total: Integer;\n" +
        "        tag: Text;\n" +
        "    begin\n" +
        "        total := 0;\n" +
        "        for i := 1 to 3 do begin\n" +
        "            total := total + i;\n" +
        "        end;\n" +
        "        tag := 'done';\n" +
        "    end;\n" +
        "}\n";

    [SkippableFact]
    public async Task Execute_CaptureValues_LoopReassignsVariable_ReportsOneEntryPerIteration()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            captureValues = true,
            coverage = true,
            code = LoopReassignsThenFinalAssignCode,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());

        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.True(tests[0].TryGetProperty("capturedValues", out var captured),
            $"expected capturedValues on the response: {r}");

        var totalEntries = captured.EnumerateArray()
            .Where(e => e.GetProperty("variableName").GetString() == "total")
            .Select(e => (Value: e.GetProperty("value").GetInt32(), StatementId: e.GetProperty("statementId").GetInt32()))
            .ToList();

        // total := 0 (the initializer, statement 0) is a DIFFERENT statement from the
        // loop body — the loop body itself runs 3 times, each producing a genuinely
        // different cumulative value (1, 3, 6), which is exactly the series ALchemist's
        // inline loop rendering needs.
        var loopEntries = totalEntries.Where(e => e.Value is 1 or 3 or 6).ToList();
        Assert.Equal(3, loopEntries.Count);
        Assert.Equal(new[] { 1, 3, 6 }, loopEntries.Select(e => e.Value).ToArray());

        // All three loop-body executions are the SAME source statement — one id, hit
        // three times — the "same statement, three executions" claim the whole issue
        // hinges on. Cross-referenced against coverage's own hit count, same technique
        // ServerExecuteMessagesTests / AlStatementTableTests already use.
        var loopStatementIds = loopEntries.Select(e => e.StatementId).Distinct().ToList();
        Assert.Single(loopStatementIds);
        var loopStatementId = loopStatementIds[0];

        Assert.True(d.TryGetProperty("coverage", out var coverage), $"expected coverage on the response: {r}");
        var statementsById = coverage.EnumerateArray().Single()
            .GetProperty("statements").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetInt32(), s => s);
        Assert.True(statementsById.ContainsKey(loopStatementId),
            $"loop statementId {loopStatementId} not in statement table: {r}");
        Assert.Equal(3, statementsById[loopStatementId].GetProperty("hits").GetInt32());

        // tag := 'done' runs on a LATER, separate statement, exactly once — proves the
        // series correctly stops repeating once the loop exits and moves on.
        var tagEntries = captured.EnumerateArray()
            .Where(e => e.GetProperty("variableName").GetString() == "tag")
            .ToList();
        var tagEntry = Assert.Single(tagEntries);
        Assert.Equal("done", tagEntry.GetProperty("value").GetString());
        Assert.NotEqual(loopStatementId, tagEntry.GetProperty("statementId").GetInt32());
    }

    // Negative direction (`.claude/rules/tdd.md`'s "both directions"): a local that is
    // declared but NEVER assigned anywhere in the scope must NOT appear in
    // capturedValues at all — under "one record per execution", an untouched local was
    // never executed into existing, so it has no execution to report (see
    // AlValueCapture's file header for why this is a deliberate change from the
    // pre-#2074 snapshot, which reported every declared local unconditionally).
    [SkippableFact]
    public async Task Execute_CaptureValues_UnassignedLocal_NeverAppearsInSeries()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            captureValues = true,
            code = "codeunit 60211 \"CV Untouched Local SX\" { trigger OnRun() " +
                   "var Touched: Integer; Untouched: Integer; begin Touched := 7; end; }",
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());

        var tests = d.GetProperty("tests");
        Assert.True(tests[0].TryGetProperty("capturedValues", out var captured),
            $"expected capturedValues on the response: {r}");
        var names = captured.EnumerateArray()
            .Select(e => e.GetProperty("variableName").GetString())
            .ToList();

        Assert.Contains("Touched", names);
        Assert.DoesNotContain("Untouched", names);
        // The touched local's own series is exactly what it should be: one execution,
        // one record.
        var touchedEntries = captured.EnumerateArray()
            .Where(e => e.GetProperty("variableName").GetString() == "Touched").ToList();
        var touchedEntry = Assert.Single(touchedEntries);
        Assert.Equal(7, touchedEntry.GetProperty("value").GetInt32());
    }

    // Negative direction, second half: captureValues omitted entirely — the field must
    // be ABSENT (not an empty array, not a stale series) even for a codeunit whose OnRun
    // contains a loop that WOULD have produced a rich series if the flag were set. Proves
    // the opt-in gate still works after the redesign — a regression here would mean
    // AlValueCapture.Enabled stopped gating OnStmtHit (the new hook this issue adds).
    [SkippableFact]
    public async Task Execute_CaptureValues_Omitted_LoopCodeunit_NoCapturedValuesField()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = LoopReassignsThenFinalAssignCode,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("pass", tests[0].GetProperty("status").GetString());
        Assert.False(tests[0].TryGetProperty("capturedValues", out _),
            $"capturedValues must be absent when captureValues wasn't requested: {r}");
    }
}
