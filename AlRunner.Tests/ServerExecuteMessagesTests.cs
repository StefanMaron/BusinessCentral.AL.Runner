using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2117 — `--server` `execute` silently discarded Message() output. Reported by
/// SShadowS/ALchemist against the published 2.7.0: an OnRun-driven codeunit calling
/// Message() four times returned exitCode:0 and test results with NO trace of any of
/// the four strings — no dispatch, no exception, nothing.
///
/// ROOT CAUSE (confirmed by decompiling Microsoft.Dynamics.Nav.Ncl.dll): NavDialog.
/// ALMessage's real body is
///     if (!session.TestExecution.TestHandleMessage(message2))
///         session.ClientCallbackOrNull?.DialogMessage(message2, automationId);
/// TestHandleMessage only ever finds a [MessageHandler] (or throws BC's own "Unhandled
/// UI") while a [Test] procedure is executing; on `execute`'s OnRun path
/// (executingTestMethod == null) it quietly returns false without throwing, and
/// ClientCallbackOrNull is null (the runner has no client) — the `?.` swallows the
/// call. Confirm()/StrMenu() read the NON-null-conditional `session.ClientCallback`,
/// which throws NavNCLCallbackNotAllowedException when there is no client instead —
/// already loud, not part of this hole (see NoHoleForConfirmOrStrMenu_ExecutePath below,
/// which pins that finding rather than assuming it).
///
/// Ghost-test guard: every positive assertion below names a SPECIFIC string, order, or
/// statement id — never just "messages present". A no-op fix (an always-empty array)
/// fails every positive fact here. An implementation that hands out the SAME
/// statementId regardless of which AL statement actually called Message() fails
/// <see cref="Execute_MessageInLoop_StatementIdDistinguishesLoopFromFinalCall"/>
/// specifically, which cross-references the id against the statement table's own
/// decoded source line (the same technique AlStatementTableTests uses) rather than just
/// checking the ids differ from each other by accident.
///
/// Spawns the real runner in --server mode; needs the BC artifact cache — reports
/// Skipped (not Passed) when absent, via TestArtifacts.
/// </summary>
public class ServerExecuteMessagesTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerExecuteMessagesTests(SharedCliServer fixture) => _fixture = fixture;

    // The issue's own reproducer, verbatim shape: a loop calling Message() three times
    // (SAME source statement, three executions) followed by one more Message() call on
    // a DIFFERENT statement.
    private const string LoopAndFinalMessageCode =
        "codeunit 60200 \"Msg Loop SX\"\n" +
        "{\n" +
        "    trigger OnRun()\n" +
        "    var\n" +
        "        i: Integer;\n" +
        "        total: Integer;\n" +
        "    begin\n" +
        "        total := 0;\n" +
        "        for i := 1 to 3 do begin\n" +
        "            total := total + i;\n" +
        "            Message('LOOP_MSG_' + Format(i));\n" +
        "        end;\n" +
        "        Message('FINAL_MSG');\n" +
        "    end;\n" +
        "}\n";

    [SkippableFact]
    public async Task Execute_MessageInLoop_ReturnsMessagesInCallOrder()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = LoopAndFinalMessageCode,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());

        Assert.True(d.TryGetProperty("messages", out var messages), $"expected messages on the response: {r}");
        var texts = messages.EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToList();
        Assert.Equal(new[] { "LOOP_MSG_1", "LOOP_MSG_2", "LOOP_MSG_3", "FINAL_MSG" }, texts);
    }

    // The statementId claim, proven the strong way: cross-referenced against the
    // statement table's own decoded source line (coverage:true on the SAME call, same
    // technique AlStatementTableTests.CapturedValueStatementId_MatchesStatementTableScopeAndId
    // uses) rather than merely asserting "the ids differ". The three loop messages come
    // from the SAME AL statement executed three times, so they must share ONE id; the
    // final message comes from a later statement, so its id must differ AND its
    // resolved line must be strictly after the loop message's resolved line.
    [SkippableFact]
    public async Task Execute_MessageInLoop_StatementIdDistinguishesLoopFromFinalCall()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            coverage = true,
            code = LoopAndFinalMessageCode,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");

        Assert.True(d.TryGetProperty("messages", out var messages), $"expected messages on the response: {r}");
        var entries = messages.EnumerateArray().ToList();
        Assert.Equal(4, entries.Count);

        var loopIds = entries.Take(3).Select(e => e.GetProperty("statementId").GetInt32()).Distinct().ToList();
        Assert.Single(loopIds); // all three loop calls are the SAME AL statement
        var loopId = loopIds[0];
        var finalId = entries[3].GetProperty("statementId").GetInt32();
        Assert.NotEqual(loopId, finalId); // the trailing call is a DIFFERENT statement
        Assert.Equal("OnRun", entries[0].GetProperty("scopeName").GetString());

        // Cross-reference: the statement table's own decoded position for each id —
        // proves statementId is a REAL position lookup, not two arbitrary distinct ints.
        Assert.True(d.TryGetProperty("coverage", out var coverage), $"expected coverage on the response: {r}");
        var statements = coverage.EnumerateArray().Single()
            .GetProperty("statements").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetInt32(), s => s);
        Assert.True(statements.ContainsKey(loopId), $"loop statementId {loopId} not in statement table: {r}");
        Assert.True(statements.ContainsKey(finalId), $"final statementId {finalId} not in statement table: {r}");
        var loopLine = statements[loopId].GetProperty("line").GetInt32();
        var finalLine = statements[finalId].GetProperty("line").GetInt32();
        Assert.True(finalLine > loopLine,
            $"FINAL_MSG's statement (line {finalLine}) should resolve strictly after the loop's (line {loopLine}): {r}");
        // The loop statement was hit 3 times (once per iteration) — proves the hit count
        // and the message count for that id genuinely agree, not coincidentally equal.
        Assert.Equal(3, statements[loopId].GetProperty("hits").GetInt32());
    }

    // Negative direction: an OnRun that never calls Message() must NOT get a `messages`
    // field at all — omitted, not an empty array. `execute` always collects (there is no
    // request-side opt-in for this field, unlike coverage/captureValues), so "absent"
    // must mean "zero messages produced", proven here rather than merely documented.
    [SkippableFact]
    public async Task Execute_NoMessageCalls_MessagesFieldAbsent()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 60201 \"No Msg SX\" { trigger OnRun() var X: Integer; begin X := 1; end; }",
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        Assert.False(d.TryGetProperty("messages", out _),
            $"messages must be absent when Message() was never called: {r}");
    }

    // THE regression guard the issue calls out by name: a [Test] procedure's Message()
    // call must still raise BC's real "Unhandled UI" when no [MessageHandler] is
    // declared — completely unaffected by RunnerClientCallback/AlMessageCapture, because
    // NavTestExecution.TestHandleMessage resolves (or throws) BEFORE NavDialog.ALMessage
    // ever consults ClientCallbackOrNull. Run via `runTests`, NOT `execute` — this is
    // the code path this fix must leave alone.
    [SkippableFact]
    public async Task RunTests_MessageWithoutHandler_StillRaisesUnhandledUI()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-msg-unhandled-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "f1a2b3c4-d5e6-4f70-8192-a3b4c5d6e7f8",
          "name": "Msg Unhandled UI Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60202, "to": 60202 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), """
        codeunit 60202 "Msg Unhandled UI SX"
        {
            Subtype = Test;

            [Test]
            procedure MessageWithoutHandler()
            begin
                Message('should not be swallowed nor silently succeed');
            end;
        }
        """);

        var server = await _fixture.GetAsync();
        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { dir },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(req);
        var (events, summary) = ProtocolV2Streaming.Split(lines);

        Assert.Single(events);
        var status = events[0].GetProperty("status").GetString();
        Assert.True(status is "fail" or "error", $"expected the unhandled Message() to fail the test, got {status}: {events[0]}");
        var message = events[0].GetProperty("message").GetString() ?? "";
        Assert.Contains("Unhandled UI", message);
        Assert.NotEqual(0, summary.GetProperty("exitCode").GetInt32());
    }

    // Documents the OTHER finding the issue asked for: Confirm()/StrMenu() on the SAME
    // `execute` path do NOT have this hole today — they already raise a real BC
    // exception (NavNCLCallbackNotAllowedException, "Callback functions are not
    // allowed") because their AL bodies read the non-null-conditional
    // `session.ClientCallback`. RunnerClientCallback reproduces that SAME exception for
    // every member except DialogMessage specifically so this stays true after the fix —
    // pinned here rather than left as an unverified claim in the PR description.
    [SkippableFact]
    public async Task Execute_ConfirmWithoutHandler_StillThrowsCallbackNotAllowed_NotSwallowed()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 60203 \"Confirm NoHandler SX\" { trigger OnRun() var Ans: Boolean; " +
                   "begin Ans := Confirm('proceed?'); end; }",
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        var message = tests[0].GetProperty("message").GetString() ?? "";
        Assert.Contains("Callback", message);
    }

    // Decompile-verified regression guard: installing RunnerClientCallback makes
    // NavSession.ClientCallbackOrNull non-null for the WHOLE session, not only inside
    // ALMessage — NavSession.set_WorkDate's real body ALSO reads it (null-checked, not
    // via the throwing property) to fire IClientCallback.WorkDateChanged, a
    // client-UI-refresh notification. `WorkDate := ...` is near-universal in BC test
    // setup, so an earlier revision of this fix that made every non-DialogMessage
    // member throw would have made this ONE line fail every such `execute` call. Proven
    // two ways in one request: the run must still pass (not throw), and Format(WorkDate)
    // fed back through the now-fixed Message() capture must show the REAL assigned
    // date, not a default/unset one.
    [SkippableFact]
    public async Task Execute_SetsWorkDate_DoesNotThrow_AndNewValueIsObservable()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 60204 \"WorkDate NoClient SX\" { trigger OnRun() " +
                   "begin WorkDate := DMY2Date(17, 3, 2031); " +
                   "Message(Format(WorkDate, 0, '<Day,2>/<Month,2>/<Year4>')); end; }",
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("pass", tests[0].GetProperty("status").GetString());

        Assert.True(d.TryGetProperty("messages", out var messages), $"expected messages on the response: {r}");
        var entries = messages.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("17/03/2031", entries[0].GetProperty("text").GetString());
    }
}
