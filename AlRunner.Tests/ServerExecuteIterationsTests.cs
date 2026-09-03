using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2056: `iterationTracking` on `--server` `execute`. ALchemist's iteration
/// stepping (SShadowS/ALchemist#1) needs, per loop, one segment per iteration carrying
/// the captured values, Message() calls and executed lines that iteration produced,
/// with nested loops linked to the enclosing loop's iteration. Until this landed the
/// only source of that data was ALchemist's own fork of this runner.
///
/// End-to-end against real compiled + executed AL, through the wire: spawns the runner
/// in --server mode (needs the BC artifact cache; reports Skipped, not Passed, when
/// absent, via TestArtifacts). The pure state-machine and classification mechanics are
/// proven without BC in AlIterationSegmenterTests / AlMemberSyntaxIndexTests; this class
/// proves the two halves meet BC's actual instrumentation.
///
/// Ghost-test guard: every positive assertion names a SPECIFIC iteration count, value,
/// message text, line or parent iteration. An implementation that emits one iteration
/// per statement hit fails every count; one that attributes every value to the newest
/// iteration fails the per-iteration value assertions; one that ignores nesting fails
/// the parentLoopId/parentIteration facts; one that flattens called procedures into
/// the caller fails the callee-instance assertions.
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
        test.GetProperty("iterations").EnumerateArray()
            .Where(l => l.GetProperty("scope").GetString() == scope).ToList();

    private static string[] Values(JsonElement step, string variable) =>
        step.GetProperty("capturedValues").EnumerateArray()
            .Where(v => v.GetProperty("variableName").GetString() == variable)
            .Select(v => v.GetProperty("value").ToString()).ToArray();

    private static int[] Lines(JsonElement step) =>
        step.GetProperty("linesExecuted").EnumerateArray().Select(l => l.GetInt32()).ToArray();

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
    public async Task Execute_ForLoop_OneStepPerIteration_WithThatIterationsValuesMessagesAndLines()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(LoopAndFinalMessageCode));

        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal("L0", loop.GetProperty("loopId").GetString());
        Assert.Equal(9, loop.GetProperty("loopLine").GetInt32());
        Assert.Equal(12, loop.GetProperty("loopEndLine").GetInt32());
        // A root loop has no parent: both fields are null-omitted (the protocol's
        // WhenWritingNull convention), never present-with-null and never a fake value.
        Assert.False(loop.TryGetProperty("parentLoopId", out _), $"root loop must not carry parentLoopId: {loop}");
        Assert.False(loop.TryGetProperty("parentIteration", out _), $"root loop must not carry parentIteration: {loop}");
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());

        var steps = loop.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, steps.Select(s => s.GetProperty("iteration").GetInt32()).ToArray());
        // Each iteration carries ITS OWN value of both locals, not the final snapshot.
        Assert.Equal(new[] { "1" }, Values(steps[0], "i"));
        Assert.Equal(new[] { "1" }, Values(steps[0], "total"));
        Assert.Equal(new[] { "2" }, Values(steps[1], "i"));
        Assert.Equal(new[] { "3" }, Values(steps[1], "total"));
        Assert.Equal(new[] { "3" }, Values(steps[2], "i"));
        Assert.Equal(new[] { "6" }, Values(steps[2], "total"));
        // The Message() of each pass lands in that pass; FINAL_MSG in none.
        for (int k = 0; k < 3; k++)
        {
            var msg = Assert.Single(steps[k].GetProperty("messages").EnumerateArray());
            Assert.Equal($"LOOP_MSG_{k + 1}", msg.GetProperty("text").GetString());
            Assert.True(msg.GetProperty("statementId").GetInt32() >= 0);
        }
        Assert.DoesNotContain(steps.SelectMany(s => s.GetProperty("messages").EnumerateArray()),
            m => m.GetProperty("text").GetString() == "FINAL_MSG");
        // Executed lines per iteration: the body's two lines, nothing before or after the loop.
        Assert.All(steps, s => Assert.Equal(new[] { 10, 11 }, Lines(s)));
        // The flat series is untouched by bucketing: still one record per execution of
        // an assigning statement, in order, `total := 0` (statement 0) included.
        var flat = t.GetProperty("capturedValues").EnumerateArray()
            .Where(v => v.GetProperty("variableName").GetString() == "total")
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
        var t = SingleTest(await ExecuteAsync(code));

        var w = Assert.Single(Loops(t, "WhileDo"));
        Assert.Equal(3, w.GetProperty("iterationCount").GetInt32()); // 4 condition evaluations, 3 passes
        var wSteps = w.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(new[] { "2" }, Values(wSteps[0], "n"));
        Assert.Equal(new[] { "1" }, Values(wSteps[1], "n"));
        Assert.Equal(new[] { "0" }, Values(wSteps[2], "n"));
        Assert.DoesNotContain(wSteps.SelectMany(s => s.GetProperty("capturedValues").EnumerateArray()),
            v => v.GetProperty("value").ToString() == "99");

        var r = Assert.Single(Loops(t, "RepeatUntil"));
        Assert.Equal(3, r.GetProperty("iterationCount").GetInt32());
        var rSteps = r.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(new[] { "1" }, Values(rSteps[0], "n"));
        Assert.Equal(new[] { "2" }, Values(rSteps[1], "n"));
        Assert.Equal(new[] { "3" }, Values(rSteps[2], "n"));
        // The until-condition's line counts as executed in the pass it ended.
        Assert.All(rSteps, s => Assert.Equal(new[] { 23, 24 }, Lines(s)));

        var fe = Assert.Single(Loops(t, "ForEachList"));
        Assert.Equal(2, fe.GetProperty("iterationCount").GetInt32());
        var feSteps = fe.GetProperty("steps").EnumerateArray().ToList();
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
        var t = SingleTest(await ExecuteAsync(code));

        var loops = Loops(t, "OnRun");
        Assert.Equal(3, loops.Count);
        var outer = loops[0];
        Assert.Equal("L0", outer.GetProperty("loopId").GetString());
        Assert.Equal(6, outer.GetProperty("loopLine").GetInt32());
        Assert.Equal(11, outer.GetProperty("loopEndLine").GetInt32());
        Assert.Equal(2, outer.GetProperty("iterationCount").GetInt32());
        var outerSteps = outer.GetProperty("steps").EnumerateArray().ToList();
        // An outer iteration's lines include the inner loop's.
        Assert.Equal(new[] { 7, 8, 9, 10 }, Lines(outerSteps[0]));
        Assert.Equal(new[] { "1" }, Values(outerSteps[0], "i"));
        Assert.Equal(new[] { "2" }, Values(outerSteps[1], "i"));

        var inner1 = loops[1];
        Assert.Equal("L1", inner1.GetProperty("loopId").GetString());
        Assert.Equal("L0", inner1.GetProperty("parentLoopId").GetString());
        Assert.Equal(1, inner1.GetProperty("parentIteration").GetInt32());
        Assert.Equal(8, inner1.GetProperty("loopLine").GetInt32());
        Assert.Equal(2, inner1.GetProperty("iterationCount").GetInt32());
        var inner1Steps = inner1.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(new[] { "1" }, Values(inner1Steps[0], "j"));
        Assert.Equal(new[] { "101" }, Values(inner1Steps[0], "s"));
        Assert.Equal(new[] { "2" }, Values(inner1Steps[1], "j"));
        Assert.Equal(new[] { "103" }, Values(inner1Steps[1], "s"));
        Assert.All(inner1Steps, s => Assert.Equal(new[] { 9 }, Lines(s)));

        var inner2 = loops[2];
        Assert.Equal("L2", inner2.GetProperty("loopId").GetString());
        Assert.Equal("L0", inner2.GetProperty("parentLoopId").GetString());
        Assert.Equal(2, inner2.GetProperty("parentIteration").GetInt32());
        var inner2Steps = inner2.GetProperty("steps").EnumerateArray().ToList();
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
        var t = SingleTest(await ExecuteAsync(code));

        var all = t.GetProperty("iterations").EnumerateArray().ToList();
        Assert.Equal(3, all.Count);
        var caller = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(2, caller.GetProperty("iterationCount").GetInt32());
        // The callee's statements never leak into the caller's iterations.
        Assert.All(caller.GetProperty("steps").EnumerateArray(), s => Assert.Equal(new[] { 7 }, Lines(s)));

        var callee = Loops(t, "Inner");
        Assert.Equal(2, callee.Count);
        Assert.Equal(caller.GetProperty("loopId").GetString(), callee[0].GetProperty("parentLoopId").GetString());
        Assert.Equal(1, callee[0].GetProperty("parentIteration").GetInt32());
        Assert.Equal(caller.GetProperty("loopId").GetString(), callee[1].GetProperty("parentLoopId").GetString());
        Assert.Equal(2, callee[1].GetProperty("parentIteration").GetInt32());
        Assert.All(callee, c => Assert.Equal(2, c.GetProperty("iterationCount").GetInt32()));
        Assert.All(callee, c => Assert.Equal(12, c.GetProperty("loopLine").GetInt32()));
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
        var t = SingleTest(await ExecuteAsync(code));

        foreach (var scope in new[] { "WithBreak", "WithExit" })
        {
            var loop = Assert.Single(Loops(t, scope));
            Assert.Equal(2, loop.GetProperty("iterationCount").GetInt32());
            var steps = loop.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(new[] { "1" }, Values(steps[0], "s"));
            Assert.Equal(new[] { "3" }, Values(steps[1], "s"));
            Assert.DoesNotContain(steps.SelectMany(s => s.GetProperty("capturedValues").EnumerateArray()),
                v => v.GetProperty("value").ToString() == "-1");
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
        Assert.Empty(loop.GetProperty("steps").EnumerateArray());
    }

    // --- negative direction ------------------------------------------------------------------------

    [SkippableFact]
    public async Task Execute_WithoutIterationTracking_HasNoIterationsField()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(LoopAndFinalMessageCode.Replace("60303", "60309"), iterationTracking: false));
        Assert.False(t.TryGetProperty("iterations", out _), $"iterations must be absent when not requested: {t}");
    }

    [SkippableFact]
    public async Task Execute_IterationTrackingWithoutCaptureValues_StillSegments_WithEmptyValueLists()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(LoopAndFinalMessageCode.Replace("60303", "60310"), captureValues: false));
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());
        var steps = loop.GetProperty("steps").EnumerateArray().ToList();
        Assert.All(steps, s => Assert.Empty(s.GetProperty("capturedValues").EnumerateArray()));
        Assert.All(steps, s => Assert.Equal(new[] { 10, 11 }, Lines(s)));
        Assert.Equal("LOOP_MSG_2", Assert.Single(steps[1].GetProperty("messages").EnumerateArray()).GetProperty("text").GetString());
    }

    [SkippableFact]
    public async Task Execute_NoLoops_ReportsAnEmptyArray_NotAbsent()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(
            "codeunit 60311 \"Iter NoLoop SX\" { trigger OnRun() var X: Integer; begin X := 1; end; }"));
        Assert.True(t.TryGetProperty("iterations", out var iterations), $"iterations must be present (empty) when requested: {t}");
        Assert.Equal(0, iterations.GetArrayLength());
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
        // iterationCount is null-omitted exactly when unsegmentable is present: no fake 0.
        Assert.False(loop.TryGetProperty("iterationCount", out _), $"unsegmentable loop must not claim a count: {loop}");
        Assert.Empty(loop.GetProperty("steps").EnumerateArray());
        Assert.Contains("no instrumented statement", loop.GetProperty("unsegmentable").GetString());
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
        // loopLine is the `for` statement's own line in the table (the entry hit once).
        var forStmt = statements.Values.Single(s => s.GetProperty("hits").GetInt32() == 1
            && s.GetProperty("line").GetInt32() == loop.GetProperty("loopLine").GetInt32());
        Assert.Equal(9, forStmt.GetProperty("line").GetInt32());
        // Every executed line of every step is a real statement line hit 3 times (once per pass).
        foreach (var step in loop.GetProperty("steps").EnumerateArray())
            foreach (var line in Lines(step))
                Assert.Contains(statements.Values, s => s.GetProperty("line").GetInt32() == line && s.GetProperty("hits").GetInt32() == 3);
        // The same file identity coverage uses.
        Assert.Equal(d.GetProperty("coverage").EnumerateArray().Single().GetProperty("file").GetString(),
            loop.GetProperty("file").GetString());
    }

    // #2056 full-fidelity contract on the steps: an iteration that ran `x := 5` while x
    // was already 5 still carries x = 5, and `flag := true` while flag was already true
    // still carries flag = true. This is the shape the iteration table renders one
    // cell per variable from, with no carry-forward on the consumer's side.
    [SkippableFact]
    public async Task Execute_SameValueAssignmentsInsideALoop_EveryIterationCarriesThem()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(
            "codeunit 60318 \"Iter Same Value SX\" { trigger OnRun() var i: Integer; x: Integer; flag: Boolean; " +
            "begin x := 5; x := 5; for i := 1 to 3 do begin x := 5; flag := true; end; x := 6; end; }"));
        var loop = Assert.Single(Loops(t, "OnRun"));
        Assert.Equal(3, loop.GetProperty("iterationCount").GetInt32());
        var steps = loop.GetProperty("steps").EnumerateArray().ToList();
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(new[] { (k + 1).ToString() }, Values(steps[k], "i"));
            Assert.Equal(new[] { "5" }, Values(steps[k], "x"));
            Assert.Equal(new[] { "True" }, Values(steps[k], "flag"));
        }
        // The pre-loop and post-loop assignments are not in any step.
        Assert.DoesNotContain(steps.SelectMany(s => s.GetProperty("capturedValues").EnumerateArray()),
            v => v.GetProperty("value").ToString() == "6");
    }

    [SkippableFact]
    public async Task Execute_LoopInsideACalledProcedure_StepsCarryTheCalleesOwnValues()
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
        var t = SingleTest(await ExecuteAsync(code));

        var caller = Assert.Single(Loops(t, "OnRun"));
        var callerSteps = caller.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(new[] { "1" }, Values(callerSteps[0], "i"));
        Assert.Equal(new[] { "3" }, Values(callerSteps[0], "s"));   // Inner() returns 1 + 2
        Assert.Equal(new[] { "2" }, Values(callerSteps[1], "i"));
        Assert.Equal(new[] { "6" }, Values(callerSteps[1], "s"));

        var callee = Loops(t, "Inner");
        Assert.Equal(2, callee.Count);
        foreach (var instance in callee)
        {
            var steps = instance.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(new[] { "1" }, Values(steps[0], "k"));
            Assert.Equal(new[] { "1" }, Values(steps[0], "s"));
            Assert.Equal(new[] { "2" }, Values(steps[1], "k"));
            Assert.Equal(new[] { "3" }, Values(steps[1], "s"));
            // Only the callee's own locals: the caller's loop variable never leaks in.
            Assert.All(steps, s => Assert.Empty(Values(s, "i")));
        }
    }

    [SkippableFact]
    public async Task Execute_ForEachOverEqualElements_EveryStepCarriesTheElement()
    {
        TestArtifacts.SkipIfMissing();
        var t = SingleTest(await ExecuteAsync(
            "codeunit 60321 \"Iter ForEach Dup SX\" { trigger OnRun() var l: List of [Integer]; v: Integer; s: Integer; " +
            "begin l.Add(5); l.Add(5); l.Add(6); foreach v in l do s := s + v; end; }"));
        var loop = Assert.Single(Loops(t, "OnRun"));
        var steps = loop.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal(new[] { "5" }, Values(steps[0], "v"));
        Assert.Equal(new[] { "5" }, Values(steps[1], "v")); // same element as before: still this pass's record
        Assert.Equal(new[] { "6" }, Values(steps[2], "v"));
        Assert.Equal(new[] { "5" }, Values(steps[0], "s"));
        Assert.Equal(new[] { "10" }, Values(steps[1], "s"));
        Assert.Equal(new[] { "16" }, Values(steps[2], "s"));
    }
}
