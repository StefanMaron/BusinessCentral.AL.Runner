using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// End-to-end tests for <c>--dap</c> (issue #1642): a real DAP TCP client
/// (<see cref="DapClient"/>) drives al-runner through initialize/launch/
/// setBreakpoints/configurationDone, proving a breakpoint actually PAUSES AL
/// execution at the requested line — not a no-op that lets the test run straight
/// through — and that the paused frame's locals reflect genuinely-live state (the
/// first statement's effect visible, the second statement's NOT yet, since BC's
/// StmtHit(N) fires BEFORE statement N's own side effect — see AlDapSession's file
/// header for why that is the CORRECT boundary for a debugger, unlike
/// --capture-values/#1640's Exit()-based design).
///
/// Uses AlRunner.Tests/Fixtures/DapBreakpoint: a [Test] method with three plain
/// assignments (Counter := 1/2/3) followed by an Assert.AreEqual(3, Counter, ...).
/// Line 21 of DapBreakpointTests.Codeunit.al is "Counter := 2;" — the second
/// statement — see that file for the exact line numbering this test depends on.
/// </summary>
public class DapServerTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapBreakpoint"));

    private const int SecondStatementLine = 21;
    private const string SourceFileName = "DapBreakpointTests.Codeunit.al";

    [SkippableFact]
    public async Task Dap_BreakpointOnSecondStatement_PausesBeforeItRuns_ThenContinueCompletesTheTest()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await DapClient.StartAsync(FixtureSrc);

        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        var initEvents = new List<JsonElement>();
        var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
        Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
        // "initialized" may have arrived before or after the response — accept both,
        // but require it to have arrived at all (the client would otherwise wait
        // forever on it in a real session).
        var sawInitialized = initEvents.Any(e => e.GetProperty("event").GetString() == "initialized")
            || (await dap.ReadUntilEventAsync("initialized")).GetProperty("event").GetString() == "initialized";
        Assert.True(sawInitialized);

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(),
            $"launch failed: {launchResp}\n--- stderr ---\n{dap.StdErr}");

        var sourcePath = Path.Combine(FixtureSrc, SourceFileName);
        var bpSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = sourcePath },
            breakpoints = new[] { new { line = SecondStatementLine } },
        });
        var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
        Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());
        var bps = bpResp.GetProperty("body").GetProperty("breakpoints");
        Assert.Equal(1, bps.GetArrayLength());
        Assert.True(bps[0].GetProperty("verified").GetBoolean(),
            $"breakpoint at line {SecondStatementLine} was not verified: {bpResp}");
        Assert.Equal(SecondStatementLine, bps[0].GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // The proof this test exists for: execution actually stops, at the line we
        // asked for, not the third or first.
        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(SecondStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        Assert.True(stResp.GetProperty("success").GetBoolean(), stResp.ToString());
        var frames = stResp.GetProperty("body").GetProperty("stackFrames");
        Assert.True(frames.GetArrayLength() >= 1, stResp.ToString());
        var topFrame = frames[0];
        Assert.Equal(SecondStatementLine, topFrame.GetProperty("line").GetInt32());
        var frameId = topFrame.GetProperty("id").GetInt32();

        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0].GetProperty("variablesReference").GetInt32();

        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        Assert.True(varResp.GetProperty("success").GetBoolean(), varResp.ToString());
        var vars = varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
        Assert.True(vars.ContainsKey("Counter"), $"no Counter local reported: {varResp}");
        // The load-bearing assertion: Counter is 1 (the FIRST statement's effect),
        // NOT 2 — proving the pause happened BEFORE the second statement's own
        // assignment ran, not after. A design that captured on StmtHit the way
        // --capture-values' first (wrong) attempt did would still show "1" here by
        // coincidence at this exact spot, but would be provably wrong at the LAST
        // statement — see AlValueCaptureTests / the file header comments for that
        // failure mode; this fact is specifically about the PAUSE boundary, not the
        // read mechanism.
        Assert.Equal("1", vars["Counter"]);

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        var exited = await dap.ReadUntilEventAsync("exited", timeout: TimeSpan.FromSeconds(60));
        // exitCode 0 means the AL test (which asserts Counter == 3 after all three
        // statements) actually passed once resumed — proving continue really let
        // execution proceed to completion, not just silence the pause.
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    [SkippableFact]
    public async Task Dap_NoBreakpointsSet_RunsStraightThrough_NoStoppedEvent()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await DapClient.StartAsync(FixtureSrc);

        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        await dap.ReadUntilResponseAsync(initSeq);
        await dap.ReadUntilEventAsync("initialized");

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(), launchResp.ToString());

        // No setBreakpoints call at all — the negative direction: with the debug
        // machinery wired but nothing armed, the AL execution must run straight
        // through with zero "stopped" events, matching AlDapSession.Enabled's
        // near-zero-cost-when-unused contract (same shape as AlCoverageTracker/
        // AlValueCapture).
        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }
}
