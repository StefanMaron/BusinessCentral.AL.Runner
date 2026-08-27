using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// End-to-end tests for <c>--dap stdio</c> (issue #2058): the same DAP session
/// <see cref="DapServerTests"/> proves over TCP, driven instead over the child
/// process's own stdin/stdout via <see cref="DapStdioClient"/> — proving parity
/// (acceptance #1: "completes the same handshake DapServerTests already covers for
/// the socket path") — plus the requirement specific to this transport (acceptance
/// #2): stdout must carry ONLY well-formed DAP frames, never a stray byte.
///
/// Reuses AlRunner.Tests/Fixtures/DapBreakpoint, same as DapServerTests.
/// </summary>
public class DapStdioServerTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapBreakpoint"));

    private const int SecondStatementLine = 21;
    private const string SourceFileName = "DapBreakpointTests.Codeunit.al";

    // Same nested-call fixture DapServerTests uses for its step-granularity tests
    // (issue #2045) — reused here rather than duplicated, see AlRunner.Tests/
    // Fixtures/DapStepping/DapSteppingTests.Codeunit.al for the line numbering these
    // constants are asserted against.
    private static readonly string SteppingFixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapStepping"));
    private const string SteppingSourceFileName = "DapSteppingTests.Codeunit.al";
    private const int OuterCallLine = 22;             // Result := Double(Result);
    private const int OuterThirdStatementLine = 23;   // Result := Result + 10;

    [SkippableFact]
    public async Task DapStdio_BreakpointOnSecondStatement_PausesBeforeItRuns_ThenContinueCompletesTheTest()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await DapStdioClient.StartAsync(FixtureSrc);

        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        var initEvents = new List<JsonElement>();
        var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
        Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
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
        // Same load-bearing assertion as DapServerTests' TCP version: paused BEFORE
        // the second statement's own assignment, not after.
        Assert.Equal("1", vars["Counter"]);

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        var exited = await dap.ReadUntilEventAsync("exited", timeout: TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());

        // Acceptance #2: no non-DAP byte reaches stdout in stdio mode. Asserting the
        // handshake above succeeded is NOT sufficient by itself — DapTransport's own
        // header parser silently skips any line without a colon (see
        // DapTransport.ReadMessageAsync), so a stray banner mixed into the stream
        // would still let this same handshake pass. Drain the child to true EOF (its
        // process exit, not just "we stopped asking"), then re-parse the ENTIRE raw
        // byte stream it wrote with an independent, zero-tolerance framer
        // (DapRawFrameValidator, proven against synthetic corruption by
        // DapRawFrameValidatorTests) and require it to decompose into EXACTLY the
        // messages this test itself decoded — nothing more, nothing less, nothing
        // extra anywhere in the stream.
        await dap.ShutdownAndDrainAsync();
        var raw = dap.RawStdoutBytes;
        Assert.True(raw.Length > 0, "expected at least one DAP frame on stdout");
        var strictFrameCount = DapRawFrameValidator.CountFramesOrThrow(raw);
        Assert.Equal(dap.MessagesReceived, strictFrameCount);
    }

    [SkippableFact]
    public async Task DapStdio_NoBreakpointsSet_RunsStraightThrough_NoStoppedEvent()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await DapStdioClient.StartAsync(FixtureSrc);

        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        await dap.ReadUntilResponseAsync(initSeq);
        await dap.ReadUntilEventAsync("initialized");

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(), launchResp.ToString());

        // No setBreakpoints call at all — the negative direction: with the debug
        // client never arming a breakpoint, the AL test runs straight through and
        // reports the SAME pass it would running headless (exit 0), never a
        // "stopped" event.
        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var allEvents = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", timeout: TimeSpan.FromSeconds(60), allEvents: allEvents);
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain(allEvents, e => e.GetProperty("event").GetString() == "stopped");
    }

    /// <summary>
    /// Issue #2070's actual root cause, one transport over: "next" releases the
    /// paused AL thread, which is then free to run, qualify, and write its own
    /// "stopped" event — racing the DAP loop thread writing the "next" response. When
    /// the AL thread wins, the wire order is "stopped" event FIRST, response SECOND.
    /// A ReadUntilResponseAsync that discards events seen while waiting for a response
    /// (DapClient's own pre-#2070-fix shape — see DapClient.cs's header comment above
    /// _pendingEvents) reads that "stopped" event, throws it away, reads the response,
    /// returns — and this test's very next ReadUntilEventAsync("stopped") then waits
    /// the full timeout for a second "stopped" that will never come. This is the exact
    /// same race #2070 fixed for the TCP client (DapClient), reproduced here over
    /// stdio because DapStdioClient shipped with its own, unfixed copy of the same
    /// read loop.
    /// </summary>
    [SkippableFact]
    public async Task DapStdio_Next_StepsOverNestedCall_LandsOnOuterNextStatement()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await DapStdioClient.StartAsync(SteppingFixtureSrc);

        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        await dap.ReadUntilResponseAsync(initSeq);
        await dap.ReadUntilEventAsync("initialized");

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(), launchResp.ToString());

        var sourcePath = Path.Combine(SteppingFixtureSrc, SteppingSourceFileName);
        var bpSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = sourcePath },
            breakpoints = new[] { new { line = OuterCallLine } },
        });
        var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
            $"breakpoint at line {OuterCallLine} was not verified: {bpResp}");

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(OuterCallLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var nextSeq = dap.SendRequest("next", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(nextSeq);

        var nextStopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal("step", nextStopped.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(OuterThirdStatementLine, nextStopped.GetProperty("body").GetProperty("line").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var exited2 = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited2.GetProperty("body").GetProperty("exitCode").GetInt32());
    }
}
