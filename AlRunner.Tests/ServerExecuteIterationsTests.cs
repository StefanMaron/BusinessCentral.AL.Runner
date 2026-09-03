using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2056: `iterationTracking` on `--server` `execute`, end to end on the wire against real
/// compiled AL. Needs the BC artifact cache (Skipped when absent). The pure mechanics
/// are covered in AlIterationSegmenterTests and AlMemberSyntaxIndexTests.
///
/// The wire does not copy values or messages into iterations: each loop's `iterations[]`
/// carry only `statements`/`lines`, and the flat `capturedValues`/`messages` records are
/// tagged with `loop` and `iteration`. The helpers below reconstruct a per-iteration view
/// by filtering the flat series on those tags, which is exactly what a consumer does.
/// </summary>
public class ServerExecuteIterationsTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerExecuteIterationsTests(SharedCliServer fixture) => _fixture = fixture;

    private async Task<JsonElement> ExecuteAsync(string code, bool iterationTracking = true, bool captureValues = true, bool coverage = false)
    {
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code,
            captureValues,
            iterationTracking,
            coverage,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        return d;
    }

    private static JsonElement SingleTest(JsonElement d)
    {
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("pass", tests[0].GetProperty("status").GetString());
        return tests[0];
    }

    private static List<JsonElement> Loops(JsonElement test, string scope) =>
        (test.TryGetProperty("loops", out var loops) ? loops.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(l => l.GetProperty("scope").GetString() == scope).ToList();

    /// <summary>A reconstructed iteration: the loop id and iteration index it filters the
    /// flat series by, plus its own statements/lines.</summary>
    private sealed record Iter(JsonElement Response, JsonElement Test, int LoopId, int Index, int[] Lines, int[] Statements);

    private static List<Iter> Steps(JsonElement d, JsonElement test, JsonElement loop)
    {
        int loopId = loop.GetProperty("id").GetInt32();
        return loop.GetProperty("iterations").EnumerateArray().Select(it => new Iter(
            d, test, loopId, it.GetProperty("index").GetInt32(),
            it.GetProperty("lines").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            it.GetProperty("statements").EnumerateArray().Select(x => x.GetInt32()).ToArray())).ToList();
    }

    private static bool Tagged(JsonElement rec, int loopId, int iteration) =>
        rec.TryGetProperty("loop", out var lp) && lp.ValueKind == JsonValueKind.Number && lp.GetInt32() == loopId
        && rec.TryGetProperty("iteration", out var it) && it.GetInt32() == iteration;

    private static IEnumerable<JsonElement> Flat(JsonElement test) =>
        test.TryGetProperty("capturedValues", out var cv) ? cv.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string[] Values(Iter s, string variable) =>
        Flat(s.Test)
            .Where(v => Tagged(v, s.LoopId, s.Index) && v.GetProperty("variableName").GetString() == variable)
            .Select(v => v.GetProperty("value").ToString()).ToArray();

    private static string[] Msgs(Iter s) =>
        (s.Response.TryGetProperty("messages", out var m) ? m.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(x => Tagged(x, s.LoopId, s.Index))
            .Select(x => x.GetProperty("text").GetString()!).ToArray();

    private static int[] Lines(Iter s) => s.Lines;

    // Every captured value anywhere in the whole response's loops (used for "not in any step").
    private static IEnumerable<string> AllLoopValues(JsonElement test) =>
        Flat(test).Where(v => v.TryGetProperty("loop", out var lp) && lp.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetProperty("value").ToString());

    // --- the issue's own reproducer -------------------------------------------------------

    private const string LoopAndFinalMessageCode =
        "codeunit 60303 \"Iter Loop SX\"\n" +
        "{\n" +
        "    trigger OnRun()\n" +
        "    var\n" +
        "        i: Integer;\n" +
        "        total: Integer;\n" +
        "    begin\n" +
        "        total := 0;\n" +
        "        for i := 1 to 3 do begin\n" +                  // line 9
        "            total := total + i;\n" +                    // line 10
        "            Message('LOOP_MSG_' + Format(i));\n" +      // line 11
        "        end;\n" +                                       // line 12
        "        Message('FINAL_MSG');\n" +
        "    end;\n" +
        "}\n";

    [SkippableFact]
    public async Task Execute_ForLoop_OneIterationPerPass_TaggedValuesMessagesAndLines()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(LoopAndFinalMessageCode);
        var t = SingleTest(d);

        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(0, loop.GetProperty("id").GetInt32());
        Assert.Equal(9, loop.GetProperty("line").GetInt32());
        Assert.Equal(12, loop.GetProperty("endLine").GetInt32());
        // A root loop has no parent: both fields are null-omitted, never a fake value.
        Assert.False(loop.TryGetProperty("parentLoop", out _), $"root loop must not carry parentLoop: {loop}");
        Assert.False(loop.TryGetProperty("parentIteration", out _), $"root loop must not carry parentIteration: {loop}");
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());

        var steps = Steps(d, t, loop);
        Assert.Equal(new[] { 1, 2, 3 }, steps.Select(s => s.Index).ToArray());
        // Each pass's own value of both locals, filtered off the flat series by the tags.
        Assert.Equal(new[] { "1" }, Values(steps[0], "i"));
        Assert.Equal(new[] { "1" }, Values(steps[0], "total"));
        Assert.Equal(new[] { "2" }, Values(steps[1], "i"));
        Assert.Equal(new[] { "3" }, Values(steps[1], "total"));
        Assert.Equal(new[] { "3" }, Values(steps[2], "i"));
        Assert.Equal(new[] { "6" }, Values(steps[2], "total"));
        // The Message() of each pass is tagged to that pass; FINAL_MSG to none.
        for (int k = 0; k < 3; k++)
            Assert.Equal(new[] { $"LOOP_MSG_{k + 1}" }, Msgs(steps[k]));
        var final = d.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("text").GetString() == "FINAL_MSG");
        Assert.False(final.TryGetProperty("loop", out _), "FINAL_MSG ran outside the loop and must carry no loop tag");
        // Executed lines per pass: the body's two lines, nothing before or after the loop.
        Assert.All(steps, s => Assert.Equal(new[] { 10, 11 }, Lines(s)));
        // The flat series is one record per execution, in order, `total := 0` included.
        var flat = Flat(t).Where(v => v.GetProperty("variableName").GetString() == "total")
            .Select(v => v.GetProperty("value").ToString()).ToArray();
        Assert.Equal(new[] { "0", "1", "3", "6" }, flat);
    }

    // --- every loop kind -------------------------------------------------------------------

    [SkippableFact]
    public async Task Execute_WhileAndRepeatAndForEach_CountIterationsFromBodyEntries_NotConditionEvaluations()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60304 \"Iter Kinds SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    begin\n" +
            "        WhileDo();\n" +
            "        RepeatUntil();\n" +
            "        ForEachList();\n" +
            "    end;\n" +
            "    local procedure WhileDo()\n" +
            "    var n: Integer;\n" +
            "    begin\n" +
            "        n := 3;\n" +
            "        while n > 0 do begin\n" +
            "            n := n - 1;\n" +
            "        end;\n" +
            "        n := 99;\n" +
            "    end;\n" +
            "    local procedure RepeatUntil()\n" +
            "    var n: Integer;\n" +
            "    begin\n" +
            "        n := 0;\n" +
            "        repeat\n" +
            "            n := n + 1;\n" +
            "        until n >= 3;\n" +
            "        n := 99;\n" +
            "    end;\n" +
            "    local procedure ForEachList()\n" +
            "    var l: List of [Integer]; v: Integer; s: Integer;\n" +
            "    begin\n" +
            "        l.Add(5);\n" +
            "        l.Add(6);\n" +
            "        foreach v in l do\n" +
            "            s := s + v;\n" +
            "        s := 99;\n" +
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);

        var w = Assert.Single(Loops(t, "WhileDo"));
        Assert.Equal(3, w.GetProperty("iterationCount").GetInt32()); // 4 condition evaluations, 3 passes
        var wSteps = Steps(d, t, w);
        Assert.Equal(new[] { "2" }, Values(wSteps[0], "n"));
        Assert.Equal(new[] { "1" }, Values(wSteps[1], "n"));
        Assert.Equal(new[] { "0" }, Values(wSteps[2], "n"));
        Assert.DoesNotContain(wSteps.SelectMany(s => Values(s, "n")), v => v == "99");

        var r = Assert.Single(Loops(t, "RepeatUntil"));
        Assert.Equal(3, r.GetProperty("iterationCount").GetInt32());
        var rSteps = Steps(d, t, r);
        // A repeat has no header hit before its first pass, so pass 1 opens with the state
        // the loop entered with (`n := 0`) and then its own assignment.
        Assert.Equal(new[] { "0", "1" }, Values(rSteps[0], "n"));
        Assert.Equal(new[] { "2" }, Values(rSteps[1], "n"));
        Assert.Equal(new[] { "3" }, Values(rSteps[2], "n"));
        Assert.All(rSteps, s => Assert.Equal(new[] { 23, 24 }, Lines(s)));

        var fe = Assert.Single(Loops(t, "ForEachList"));
        Assert.Equal(2, fe.GetProperty("iterationCount").GetInt32());
        var feSteps = Steps(d, t, fe);
        Assert.Equal(new[] { "5" }, Values(feSteps[0], "v"));
        Assert.Equal(new[] { "5" }, Values(feSteps[0], "s"));
        Assert.Equal(new[] { "6" }, Values(feSteps[1], "v"));
        Assert.Equal(new[] { "11" }, Values(feSteps[1], "s"));
    }

    // --- nesting: lexical and across a procedure call ------------------------------------

    [SkippableFact]
    public async Task Execute_NestedLoops_OneInnerInstancePerOuterIteration_LinkedToIt()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60305 \"Iter Nested SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var i: Integer; j: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 2 do begin\n" +          // line 6
            "            s := s + 100;\n" +
            "            for j := 1 to 2 do\n" +             // line 8
            "                s := s + j;\n" +                // line 9
            "            s := s + 1000;\n" +
            "        end;\n" +                               // line 11
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);

        var loops = Loops(t, "OnRun");
        Assert.Equal(3, loops.Count);
        var outer = loops[0];
        Assert.Equal(0, outer.GetProperty("id").GetInt32());
        Assert.Equal(6, outer.GetProperty("line").GetInt32());
        Assert.Equal(11, outer.GetProperty("endLine").GetInt32());
        Assert.Equal(2, outer.GetProperty("iterationCount").GetInt32());
        var outerSteps = Steps(d, t, outer);
        Assert.Equal(new[] { 7, 8, 9, 10 }, Lines(outerSteps[0])); // includes the inner loop's line
        Assert.Equal(new[] { "1" }, Values(outerSteps[0], "i"));
        Assert.Equal(new[] { "2" }, Values(outerSteps[1], "i"));

        var inner1 = loops[1];
        Assert.Equal(1, inner1.GetProperty("id").GetInt32());
        Assert.Equal(0, inner1.GetProperty("parentLoop").GetInt32());
        Assert.Equal(1, inner1.GetProperty("parentIteration").GetInt32());
        Assert.Equal(8, inner1.GetProperty("line").GetInt32());
        Assert.Equal(2, inner1.GetProperty("iterationCount").GetInt32());
        var inner1Steps = Steps(d, t, inner1);
        Assert.Equal(new[] { "1" }, Values(inner1Steps[0], "j"));
        Assert.Equal(new[] { "101" }, Values(inner1Steps[0], "s"));
        Assert.Equal(new[] { "2" }, Values(inner1Steps[1], "j"));
        Assert.Equal(new[] { "103" }, Values(inner1Steps[1], "s"));
        Assert.All(inner1Steps, s => Assert.Equal(new[] { 9 }, Lines(s)));

        var inner2 = loops[2];
        Assert.Equal(2, inner2.GetProperty("id").GetInt32());
        Assert.Equal(0, inner2.GetProperty("parentLoop").GetInt32());
        Assert.Equal(2, inner2.GetProperty("parentIteration").GetInt32());
        var inner2Steps = Steps(d, t, inner2);
        Assert.Equal(new[] { "1204" }, Values(inner2Steps[0], "s"));
        Assert.Equal(new[] { "1206" }, Values(inner2Steps[1], "s"));
    }

    [SkippableFact]
    public async Task Execute_LoopInsideACalledProcedure_IsItsOwnInstance_ParentedToTheCallersIteration()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60306 \"Iter Callee SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var i: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 2 do\n" +
            "            s := s + Inner();\n" +
            "    end;\n" +
            "    local procedure Inner(): Integer\n" +
            "    var k: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for k := 1 to 2 do\n" +
            "            s := s + k;\n" +
            "        exit(s);\n" +
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);

        Assert.Equal(3, t.GetProperty("loops").GetArrayLength());
        var caller = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(2, caller.GetProperty("iterationCount").GetInt32());
        Assert.All(Steps(d, t, caller), s => Assert.Equal(new[] { 7 }, Lines(s)));

        var callee = Loops(t, "Inner");
        Assert.Equal(2, callee.Count);
        int callerId = caller.GetProperty("id").GetInt32();
        Assert.Equal(callerId, callee[0].GetProperty("parentLoop").GetInt32());
        Assert.Equal(1, callee[0].GetProperty("parentIteration").GetInt32());
        Assert.Equal(callerId, callee[1].GetProperty("parentLoop").GetInt32());
        Assert.Equal(2, callee[1].GetProperty("parentIteration").GetInt32());
        Assert.All(callee, c => Assert.Equal(2, c.GetProperty("iterationCount").GetInt32()));
        Assert.All(callee, c => Assert.Equal(12, c.GetProperty("line").GetInt32()));
        // Distinct loop ids across the response.
        var ids = t.GetProperty("loops").EnumerateArray().Select(l => l.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(ids.Distinct().Count(), ids.Length);
    }

    // --- early exits and empty loops -------------------------------------------------------------

    [SkippableFact]
    public async Task Execute_BreakAndExit_CloseTheLastIterationWithItsValues()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60307 \"Iter EarlyExit SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    begin\n" +
            "        WithBreak();\n" +
            "        WithExit();\n" +
            "    end;\n" +
            "    local procedure WithBreak()\n" +
            "    var i: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 10 do begin\n" +
            "            s := s + i;\n" +
            "            if i = 2 then\n" +
            "                break;\n" +
            "        end;\n" +
            "        s := -1;\n" +
            "    end;\n" +
            "    local procedure WithExit()\n" +
            "    var i: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 10 do begin\n" +
            "            s := s + i;\n" +
            "            if i = 2 then\n" +
            "                exit;\n" +
            "        end;\n" +
            "        s := -1;\n" +
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);

        foreach (var scope in new[] { "WithBreak", "WithExit" })
        {
            var loop = Assert.Single(Loops(t, scope));
            Assert.Equal(2, loop.GetProperty("iterationCount").GetInt32());
            var steps = Steps(d, t, loop);
            Assert.Equal(new[] { "1" }, Values(steps[0], "s"));
            Assert.Equal(new[] { "3" }, Values(steps[1], "s"));
            Assert.DoesNotContain(steps.SelectMany(s => Values(s, "s")), v => v == "-1");
        }
    }

    [SkippableFact]
    public async Task Execute_LoopThatNeverRuns_IsReportedWithZeroIterations_NotOmitted()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60308 \"Iter Zero SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var i: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 0 do\n" +
            "            s := s + 1;\n" +
            "        s := 7;\n" +
            "    end;\n" +
            "}\n";
        var t = SingleTest(await ExecuteAsync(code));
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(0, loop.GetProperty("iterationCount").GetInt32());
        Assert.Empty(loop.GetProperty("iterations").EnumerateArray());
    }

    // --- negative direction ------------------------------------------------------------------------

    [SkippableFact]
    public async Task Execute_WithoutIterationTracking_HasNoLoopsField()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(LoopAndFinalMessageCode.Replace("60303", "60309"), iterationTracking: false));
        Assert.False(t.TryGetProperty("loops", out _), $"loops must be absent when not requested: {t}");
    }

    [SkippableFact]
    public async Task Execute_IterationTrackingWithoutCaptureValues_StillSegments_WithNoTaggedValues()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(LoopAndFinalMessageCode.Replace("60303", "60310"), captureValues: false);
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());
        var steps = Steps(d, t, loop);
        Assert.All(steps, s => Assert.Empty(Values(s, "total")));   // no capturedValues at all
        Assert.All(steps, s => Assert.Equal(new[] { 10, 11 }, Lines(s)));
        Assert.Equal(new[] { "LOOP_MSG_2" }, Msgs(steps[1]));       // messages are still tagged
    }

    [SkippableFact]
    public async Task Execute_NoLoops_ReportsAnEmptyArray_NotAbsent()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(
            "codeunit 60311 \"Iter NoLoop SX\" { trigger OnRun() var X: Integer; begin X := 1; end; }"));
        Assert.True(t.TryGetProperty("loops", out var loops), $"loops must be present (empty) when requested: {t}");
        Assert.Equal(0, loops.GetArrayLength());
    }

    [SkippableFact]
    public async Task Execute_EmptyLoopBody_IsReportedAsUnsegmentable_NeverAsAFakeCount()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60312 \"Iter EmptyBody SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var i: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 3 do ;\n" +
            "        s := 7;\n" +
            "    end;\n" +
            "}\n";
        var t = SingleTest(await ExecuteAsync(code));
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.False(loop.TryGetProperty("iterationCount", out _), $"unsegmentable loop must not claim a count: {loop}");
        Assert.Empty(loop.GetProperty("iterations").EnumerateArray());
        Assert.Equal("emptyBody", loop.GetProperty("unsegmentable").GetString());
    }

    // --- cross-reference with the statement table --------------------------------------------------

    [SkippableFact]
    public async Task Execute_LoopLinesAndExecutedLines_AgreeWithTheStatementTable()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(LoopAndFinalMessageCode.Replace("60303", "60313"), coverage: true);
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));

        var statements = d.GetProperty("coverage").EnumerateArray().Single()
            .GetProperty("statements").EnumerateArray()
            .Where(s => s.GetProperty("scope").GetString() == "OnRun")
            .ToDictionary(s => s.GetProperty("id").GetInt32(), s => s);
        // line is the `for` statement's own line in the table (the entry hit once).
        var forStmt = statements.Values.Single(s => s.GetProperty("hits").GetInt32() == 1
            && s.GetProperty("line").GetInt32() == loop.GetProperty("line").GetInt32());
        Assert.Equal(9, forStmt.GetProperty("line").GetInt32());
        foreach (var step in Steps(d, t, loop))
            foreach (var line in Lines(step))
                Assert.Contains(statements.Values, s => s.GetProperty("line").GetInt32() == line && s.GetProperty("hits").GetInt32() == 3);
        Assert.Equal(d.GetProperty("coverage").EnumerateArray().Single().GetProperty("file").GetString(),
            loop.GetProperty("file").GetString());
    }

    // #2056 full-fidelity contract: a pass that ran `x := 5` while x was already 5 still
    // carries x = 5, and `flag := true` while flag was already true still carries it.
    [SkippableFact]
    public async Task Execute_SameValueAssignmentsInsideALoop_EveryIterationCarriesThem()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(
            "codeunit 60318 \"Iter Same Value SX\" { trigger OnRun() var i: Integer; x: Integer; flag: Boolean; " +
            "begin x := 5; x := 5; for i := 1 to 3 do begin x := 5; flag := true; end; x := 6; end; }");
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());
        var steps = Steps(d, t, loop);
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(new[] { (k + 1).ToString() }, Values(steps[k], "i"));
            Assert.Equal(new[] { "5" }, Values(steps[k], "x"));
            Assert.Equal(new[] { "True" }, Values(steps[k], "flag"));
        }
        // The pre-loop and post-loop assignments carry no loop tag.
        Assert.DoesNotContain(AllLoopValues(t), v => v == "6");
    }

    [SkippableFact]
    public async Task Execute_LoopInsideACalledProcedure_IterationsCarryTheCalleesOwnValues()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60319 \"Iter Callee Values SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var i: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 2 do\n" +
            "            s := s + Inner();\n" +
            "    end;\n" +
            "    local procedure Inner(): Integer\n" +
            "    var k: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for k := 1 to 2 do\n" +
            "            s := s + k;\n" +
            "        exit(s);\n" +
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);

        var caller = Assert.Single(Loops(t, "OnRun"));
        var callerSteps = Steps(d, t, caller);
        Assert.Equal(new[] { "1" }, Values(callerSteps[0], "i"));
        Assert.Equal(new[] { "3" }, Values(callerSteps[0], "s"));   // Inner() returns 1 + 2
        Assert.Equal(new[] { "2" }, Values(callerSteps[1], "i"));
        Assert.Equal(new[] { "6" }, Values(callerSteps[1], "s"));

        var callee = Loops(t, "Inner");
        Assert.Equal(2, callee.Count);
        foreach (var instance in callee)
        {
            var steps = Steps(d, t, instance);
            Assert.Equal(new[] { "1" }, Values(steps[0], "k"));
            Assert.Equal(new[] { "1" }, Values(steps[0], "s"));
            Assert.Equal(new[] { "2" }, Values(steps[1], "k"));
            Assert.Equal(new[] { "3" }, Values(steps[1], "s"));
            Assert.All(steps, s => Assert.Empty(Values(s, "i")));   // caller's loop variable never leaks in
        }
    }

    [SkippableFact]
    public async Task Execute_ForEachOverEqualElements_EveryIterationCarriesTheElement()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(
            "codeunit 60321 \"Iter ForEach Dup SX\" { trigger OnRun() var l: List of [Integer]; v: Integer; s: Integer; " +
            "begin l.Add(5); l.Add(5); l.Add(6); foreach v in l do s := s + v; end; }");
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));
        var steps = Steps(d, t, loop);
        Assert.Equal(3, steps.Count);
        Assert.Equal(new[] { "5" }, Values(steps[0], "v"));
        Assert.Equal(new[] { "5" }, Values(steps[1], "v")); // same element as before: still this pass's record
        Assert.Equal(new[] { "6" }, Values(steps[2], "v"));
        Assert.Equal(new[] { "5" }, Values(steps[0], "s"));
        Assert.Equal(new[] { "10" }, Values(steps[1], "s"));
        Assert.Equal(new[] { "16" }, Values(steps[2], "s"));
    }

    // --- round 3 -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Execute_SoleBodyNestedFor_EachOuterPassGetsItsOwnInnerInstance()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(
            "codeunit 60323 \"Iter Sole Nested SX\" { trigger OnRun() var i: Integer; j: Integer; s: Integer; " +
            "begin for i := 1 to 2 do for j := 1 to 2 do s := s + 10 * i + j; end; }");
        var t = SingleTest(d);
        var loops = Loops(t, "OnRun");
        Assert.Equal(3, loops.Count);
        Assert.Equal(2, loops[0].GetProperty("iterationCount").GetInt32());
        var inner1 = Steps(d, t, loops[1]);
        var inner2 = Steps(d, t, loops[2]);
        Assert.Equal(1, loops[1].GetProperty("parentIteration").GetInt32());
        Assert.Equal(2, loops[2].GetProperty("parentIteration").GetInt32());
        Assert.Equal(new[] { "11" }, Values(inner1[0], "s"));
        Assert.Equal(new[] { "23" }, Values(inner1[1], "s"));
        Assert.Equal(new[] { "44" }, Values(inner2[0], "s"));
        Assert.Equal(new[] { "66" }, Values(inner2[1], "s"));
    }

    [SkippableFact]
    public async Task Execute_ErrorInsideACalleeLoop_CaughtByTheCaller_DoesNotCorruptTheCallersLoop()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60324 \"Iter Caught Error SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var i: Integer; caught: Integer;\n" +
            "    begin\n" +
            "        for i := 1 to 2 do begin\n" +
            "            if not Risky() then\n" +
            "                caught += 1;\n" +
            "        end;\n" +
            "    end;\n" +
            "    [TryFunction]\n" +
            "    local procedure Risky()\n" +
            "    var k: Integer;\n" +
            "    begin\n" +
            "        for k := 1 to 3 do\n" +
            "            if k = 2 then\n" +
            "                Error('boom');\n" +
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);
        var caller = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(2, caller.GetProperty("iterationCount").GetInt32());
        Assert.Equal("exit", caller.GetProperty("closedBy").GetString());
        var callerSteps = Steps(d, t, caller);
        Assert.Equal(new[] { "1" }, Values(callerSteps[0], "caught"));
        Assert.Equal(new[] { "2" }, Values(callerSteps[1], "caught"));
        var callee = Loops(t, "Risky");
        Assert.Equal(2, callee.Count);
        int callerId = caller.GetProperty("id").GetInt32();
        Assert.All(callee, c => Assert.Equal(callerId, c.GetProperty("parentLoop").GetInt32()));
        Assert.Equal(1, callee[0].GetProperty("parentIteration").GetInt32());
        Assert.Equal(2, callee[1].GetProperty("parentIteration").GetInt32());
        Assert.All(callee, c => Assert.Equal(2, c.GetProperty("iterationCount").GetInt32()));
    }

    [SkippableFact]
    public async Task Execute_LoopInsideAWhileCondition_IsParentedToThePassTheConditionOpens()
    {
        TestArtifacts.SkipIfMissing();
        const string code =
            "codeunit 60325 \"Iter Cond Loop SX\"\n" +
            "{\n" +
            "    trigger OnRun()\n" +
            "    var n: Integer;\n" +
            "    begin\n" +
            "        while More(n) do begin\n" +
            "            n += 1;\n" +
            "            Message('PASS_' + Format(n));\n" +
            "        end;\n" +
            "    end;\n" +
            "    local procedure More(n: Integer): Boolean\n" +
            "    var k: Integer; s: Integer;\n" +
            "    begin\n" +
            "        for k := 1 to 2 do\n" +
            "            s += k;\n" +
            "        Message('COND_' + Format(n));\n" +
            "        exit(n < 2);\n" +
            "    end;\n" +
            "}\n";
        var d = await ExecuteAsync(code);
        var t = SingleTest(d);
        var outer = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(2, outer.GetProperty("iterationCount").GetInt32());
        var steps = Steps(d, t, outer);
        // The condition's Message() is tagged to the pass that evaluation opened; the
        // terminating evaluation's to the last pass.
        Assert.Equal(new[] { "COND_0", "PASS_1" }, Msgs(steps[0]));
        Assert.Equal(new[] { "COND_1", "PASS_2", "COND_2" }, Msgs(steps[1]));
        var inner = Loops(t, "More");
        Assert.Equal(3, inner.Count);
        Assert.Equal(1, inner[0].GetProperty("parentIteration").GetInt32());
        Assert.Equal(2, inner[1].GetProperty("parentIteration").GetInt32());
        Assert.Equal(2, inner[2].GetProperty("parentIteration").GetInt32());
    }

    [SkippableFact]
    public async Task Execute_ZeroIterationNestedFor_ItsLoopVariableLandsInTheEnclosingPass()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(
            "codeunit 60326 \"Iter Zero Nested SX\" { trigger OnRun() var i: Integer; j: Integer; y: Integer; " +
            "begin for i := 1 to 2 do begin for j := 1 to 0 do y += 100; y += 1; end; end; }");
        var t = SingleTest(d);
        var loops = Loops(t, "OnRun");
        Assert.Equal(3, loops.Count);
        Assert.All(loops.Skip(1), l => Assert.Equal(0, l.GetProperty("iterationCount").GetInt32()));
        var outerSteps = Steps(d, t, loops[0]);
        Assert.Equal(new[] { "1" }, Values(outerSteps[0], "j"));
        Assert.Equal(new[] { "1" }, Values(outerSteps[0], "y"));
    }

    [SkippableFact]
    public async Task Execute_LoopVariableInMixedCase_StillPlaced()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(
            "codeunit 60327 \"Iter Case SX\" { trigger OnRun() var I: Integer; s: Integer; " +
            "begin for i := 1 to 2 do s := s + i; end; }");
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));
        var steps = Steps(d, t, loop);
        Assert.Equal(new[] { "1" }, Values(steps[0], "I"));
        Assert.Equal(new[] { "2" }, Values(steps[1], "I"));
    }

    [SkippableFact]
    public async Task Execute_RecordDrivenRepeat_EveryPassCarriesTheRecordPosition()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-iter-record", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LoopRows.Table.al"), """
        table 60328 "Iter Loop Rows SX"
        {
            fields { field(1; Number; Integer) { } }
            keys { key(PK; Number) { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "RecLoop.Codeunit.al"), """
        codeunit 60329 "Iter Record Loop SX"
        {
            trigger OnRun()
            var
                Rec: Record "Iter Loop Rows SX" temporary;
                total: Integer;
            begin
                Rec.Number := 10; Rec.Insert();
                Rec.Number := 20; Rec.Insert();
                Rec.Number := 30; Rec.Insert();
                if Rec.FindSet() then
                    repeat
                        total += Rec.Number;
                    until Rec.Next() = 0;
            end;
        }
        """);
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            sourcePaths = new[] { dir },
            captureValues = true,
            iterationTracking = true,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());
        var steps = Steps(d, t, loop);
        // A record's captured value is its primary key; FindSet positioned it before the
        // loop, Next() repositions it in the until-condition that opens each later pass.
        Assert.Equal(new[] { "10" }, Values(steps[0], "Rec"));
        Assert.Equal(new[] { "20" }, Values(steps[1], "Rec"));
        Assert.Equal(new[] { "30" }, Values(steps[2], "Rec"));
        Assert.Equal(new[] { "10" }, Values(steps[0], "total"));
        Assert.Equal(new[] { "30" }, Values(steps[1], "total"));
        Assert.Equal(new[] { "60" }, Values(steps[2], "total"));
        Assert.Equal("scopeExit", loop.GetProperty("closedBy").GetString());
    }

    [SkippableFact]
    public async Task Execute_LoopRecord_CarriesColumnsStatementsAndClosedBy()
    {
        TestArtifacts.SkipIfMissing();
        var d = await ExecuteAsync(LoopAndFinalMessageCode.Replace("60303", "60330"), coverage: true);
        var t = SingleTest(d);
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(9, loop.GetProperty("line").GetInt32());
        Assert.Equal(9, loop.GetProperty("column").GetInt32());
        Assert.Equal(12, loop.GetProperty("endLine").GetInt32());
        Assert.True(loop.GetProperty("endColumn").GetInt32() > 1);
        Assert.Equal("exit", loop.GetProperty("closedBy").GetString());
        Assert.False(t.TryGetProperty("unresolvedScopes", out _));
        var ids = d.GetProperty("coverage").EnumerateArray().Single().GetProperty("statements").EnumerateArray()
            .Select(s => s.GetProperty("id").GetInt32()).ToHashSet();
        foreach (var it in loop.GetProperty("iterations").EnumerateArray())
        {
            var executed = it.GetProperty("statements").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(2, executed.Length);
            Assert.All(executed, id => Assert.Contains(id, ids));
        }
    }

    [SkippableFact]
    public async Task Execute_LoopInATableTrigger_IsTrackedUnderItsQualifiedScopeName()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-iter-trigger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Trig.Table.al"), """
        table 60331 "Iter Trigger SX"
        {
            fields
            {
                field(1; Number; Integer)
                {
                    trigger OnValidate()
                    var
                        k: Integer;
                        t: Integer;
                    begin
                        for k := 1 to 2 do
                            t += k;
                    end;
                }
            }
            keys { key(PK; Number) { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Trig.Codeunit.al"), """
        codeunit 60332 "Iter Trigger Run SX"
        {
            trigger OnRun()
            var
                Rec: Record "Iter Trigger SX" temporary;
                i: Integer;
            begin
                for i := 1 to 2 do
                    Rec.Validate(Number, i);
            end;
        }
        """);
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            sourcePaths = new[] { dir },
            captureValues = true,
            iterationTracking = true,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        var t = SingleTest(d);
        var caller = Assert.Single(Loops(t, "OnRun"));
        var trigger = Loops(t, "Number - OnValidate");
        Assert.Equal(2, trigger.Count);
        Assert.False(t.TryGetProperty("unresolvedScopes", out _), $"the trigger scope must resolve: {t}");
        int callerId = caller.GetProperty("id").GetInt32();
        Assert.All(trigger, l => Assert.Equal(callerId, l.GetProperty("parentLoop").GetInt32()));
        Assert.Equal(1, trigger[0].GetProperty("parentIteration").GetInt32());
        Assert.Equal(2, trigger[1].GetProperty("parentIteration").GetInt32());
        Assert.All(trigger, l => Assert.Equal(2, l.GetProperty("iterationCount").GetInt32()));
        Assert.All(trigger, l => Assert.EndsWith("Trig.Table.al", l.GetProperty("file").GetString()));
        foreach (var l in trigger)
        {
            var steps = Steps(d, t, l);
            Assert.Equal(new[] { "1" }, Values(steps[0], "k"));
            Assert.Equal(new[] { "1" }, Values(steps[0], "t"));
            Assert.Equal(new[] { "2" }, Values(steps[1], "k"));
            Assert.Equal(new[] { "3" }, Values(steps[1], "t"));
        }
        var callerSteps = Steps(d, t, caller);
        Assert.Equal(new[] { "1" }, Values(callerSteps[0], "Rec"));
        Assert.Equal(new[] { "2" }, Values(callerSteps[1], "Rec"));
    }
}
