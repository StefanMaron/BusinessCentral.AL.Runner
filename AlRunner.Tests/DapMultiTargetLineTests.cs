// DapMultiTargetLineTests — #3820: a DAP breakpoint bound ONE statement per source line.
//
// `DapBreakpointResolver.Resolve` returned one (ScopeType, StatementIndex) per request and
// stopped at the first exact line match. AL permits more than one executable statement on one
// physical line — AlCoverageTracker.CollectStatementTable's own doc comment says so, and keeps
// such statements apart by id and column precisely because a line cannot — so binding one of
// them means the breakpoint fires only if execution reaches the one that was picked.
//
// Which one that was is arbitrary rather than "the first in the file": the instrumented
// statement indexes come out of a HashSet, and assembly and type enumeration order is not a
// semantic ordering. So the defect is not that a particular target wins; it is that the others
// are not armed at all.
//
// These facts drive a real DAP session against Fixtures/DapMultiTarget, whose line 29 carries
// two assignments that both execute, with line 28 as the single-statement control.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapMultiTargetLineTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapMultiTarget"));

    private const string SourceFileName = "MultiTarget.Codeunit.al";

    // Fixtures/DapMultiTarget/MultiTarget.Codeunit.al, asserted against exactly.
    private const int SharedLine = 29;       // Second := 2; First := First + Second;
    private const int SingleStatementLine = 28; // First := 1;

    /// <summary>Drives initialize / launch / setBreakpoints / configurationDone and returns the
    /// live client together with the setBreakpoints response.</summary>
    private static async Task<(DapClient Dap, JsonElement BpResponse)> StartAndSetBreakpointAsync(int line)
    {
        var dap = await DapClient.StartAsync(FixtureSrc);
        try
        {
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

            var bpSeq = dap.SendRequest("setBreakpoints", new
            {
                source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
                breakpoints = new[] { new { line } },
            });
            var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
            Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());
            return (dap, bpResp);
        }
        catch
        {
            await dap.DisposeAsync();
            throw;
        }
    }

    /// <summary>Reads the integer locals of the top frame of the current pause.</summary>
    private static async Task<Dictionary<string, string?>> ReadTopFrameLocalsAsync(DapClient dap)
    {
        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var frameId = stResp.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0]
            .GetProperty("variablesReference").GetInt32();
        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        return varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
    }

    /// <summary>
    /// RED before the fix: exactly ONE stop. Line 29 carries two assignments and both execute,
    /// so a breakpoint there has two targets; binding one of them means the client stops once
    /// and the other statement runs past a breakpoint it was told is set.
    ///
    /// GREEN: two stops, in execution order, and the locals prove WHICH statement each one is
    /// in front of — the pause is before the statement's own effect, so at the first stop
    /// Second is still 0, and at the second it is 2 while First is still 1. That is what makes
    /// this more than a count: an implementation that armed one target twice would give two
    /// stops with identical locals.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointOnALineWithTwoStatements_StopsAtEachOfThem()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SharedLine);
        await using var _ = dap;

        // One breakpoint per request on the wire, whatever the line binds to — the protocol
        // requires the response to match the request one for one.
        var bps = bpResp.GetProperty("body").GetProperty("breakpoints");
        Assert.Equal(1, bps.GetArrayLength());
        Assert.True(bps[0].GetProperty("verified").GetBoolean(), bpResp.ToString());
        Assert.Equal(SharedLine, bps[0].GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var first = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal("breakpoint", first.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(SharedLine, first.GetProperty("body").GetProperty("line").GetInt32());
        var atFirst = await ReadTopFrameLocalsAsync(dap);
        Assert.Equal("1", atFirst["First"]);
        Assert.Equal("0", atFirst["Second"]);

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        var second = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(30));
        Assert.Equal("breakpoint", second.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(SharedLine, second.GetProperty("body").GetProperty("line").GetInt32());
        var atSecond = await ReadTopFrameLocalsAsync(dap);
        Assert.Equal("1", atSecond["First"]);
        Assert.Equal("2", atSecond["Second"]);

        var contSeq2 = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq2);

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The control, and the reason the fact above is about multiple TARGETS rather than about
    /// stopping twice: a line carrying one statement must still stop exactly once. An
    /// implementation that registered a line's statements more than once, or that re-armed on
    /// continue, would pass the fact above and fail here.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointOnALineWithOneStatement_StopsExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SingleStatementLine);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(), bpResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SingleStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }
}
