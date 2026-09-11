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
// These facts drive a real DAP session against Fixtures/DapMultiTarget, which carries four
// shapes: two statements on one line (36), three on one line (37), two one-line procedures on
// one line (26, so the targets are in two different scopes), and a single-statement control
// (35). The column facts use line 36, whose second statement starts at column 22.
//
// The column half is #3879 review: arming every target is right for a gutter breakpoint and
// wrong for an INLINE one, which is what DAP SourceBreakpoint.column means.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapMultiTargetLineTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapMultiTarget"));

    private const string SourceFileName = "MultiTarget.Codeunit.al";

    // Fixtures/DapMultiTarget/MultiTarget.Codeunit.al, asserted against exactly.
    private const int SharedLine = 36;          // Second := 2; First := First + Second;
    private const int SingleStatementLine = 35; // First := 1;
    private const int ThreeStatementLine = 37;  // Third := 1; Third := Third + 1; Third := Third + 1;
    private const int TwoProceduresLine = 26;   // procedure Seven() ... end;  procedure Nine() ... end;

    /// <summary>The 1-based column <c>First := First + Second;</c> starts at on
    /// <see cref="SharedLine"/> — DAP's inline-breakpoint coordinate for the SECOND of that
    /// line's two statements.</summary>
    private const int SecondStatementColumn = 22;

    /// <summary>Drives initialize / launch / setBreakpoints / configurationDone and returns the
    /// live client together with the setBreakpoints response.</summary>
    private static async Task<(DapClient Dap, JsonElement BpResponse)> StartAndSetBreakpointAsync(
        int line, int? column = null)
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
                breakpoints = new[] { new { line, column } },
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

    /// <summary>
    /// #3879 review: the base fact proves TWO targets in ONE scope, and an implementation
    /// that armed at most two, or that handled only same-scope fan-out, would pass it. Line 37
    /// carries three statements, all of which run, so this pins "every match" rather than "at
    /// least two".
    ///
    /// Third counts 1, 2, 3 across the three, and the locals at each stop say which statement
    /// is next — so three stops with the same value would not pass.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointOnALineWithThreeStatements_StopsAtEachOfThem()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(ThreeStatementLine);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(), bpResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // Before each of the three statements: Third is 0, then 1, then 2.
        foreach (var expected in new[] { "0", "1", "2" })
        {
            var stopped = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(60));
            Assert.Equal(ThreeStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());
            var locals = await ReadTopFrameLocalsAsync(dap);
            Assert.Equal(expected, locals["Third"]);
            var contSeq = dap.SendRequest("continue", new { threadId = 1 });
            await dap.ReadUntilResponseAsync(contSeq);
        }

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// #3879 review: fan-out ACROSS scope types, which the same-scope facts cannot show. Line
    /// 26 declares two one-line procedures, so its two targets live in two different emitted
    /// scope classes; both are called, so both must stop.
    ///
    /// This is the shape the old code could not serve at all — it picked one scope and left
    /// the other unarmed — and it is the case the issue named as reachable across objects
    /// since a file's objects became separately addressable.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointOnALineWithTwoOneLineProcedures_StopsInsideEachOfThem()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(TwoProceduresLine);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(),
            $"a line declaring two one-line procedures did not verify: {bpResp}\n"
            + $"--- stderr ---\n{dap.StdErr}");

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // Seven() then Nine(), in call order. The frame NAME is what proves these are two
        // different scopes rather than one scope hit twice.
        foreach (var expectedFrame in new[] { "Seven", "Nine" })
        {
            var stopped = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(60));
            Assert.Equal(TwoProceduresLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

            var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
            var stResp = await dap.ReadUntilResponseAsync(stSeq);
            var frameName = stResp.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("name").GetString() ?? "";
            Assert.True(frameName.Contains(expectedFrame, StringComparison.OrdinalIgnoreCase),
                $"paused frame is '{frameName}', not {expectedFrame} — the two targets on this "
                + $"line are not in two different scopes: {stResp}");

            var contSeq = dap.SendRequest("continue", new { threadId = 1 });
            await dap.ReadUntilResponseAsync(contSeq);
        }

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// #3879 review: arming every target on the line is right for a gutter breakpoint and
    /// WRONG for an inline one. DAP's <c>SourceBreakpoint.column</c> is how a client says
    /// "this statement, not the others on the line" — VS Code's inline breakpoints exist for
    /// exactly the multiple-statements-per-line case — and the handler used to discard it.
    ///
    /// Column 22 on line 36 is the second statement. So: one stop, not two, and at the
    /// SECOND statement — Second is already 2 when it pauses, which the first statement's
    /// pause would report as 0.
    /// </summary>
    [SkippableFact]
    public async Task InlineBreakpointNamingAColumn_ArmsOnlyThatStatement()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SharedLine, SecondStatementColumn);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(), bpResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SharedLine, stopped.GetProperty("body").GetProperty("line").GetInt32());
        var locals = await ReadTopFrameLocalsAsync(dap);
        Assert.Equal("2", locals["Second"]);
        Assert.Equal("1", locals["First"]);

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        // No second stop on that line: the other statement was not armed.
        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The other direction of the column rule: a column that names no statement start must
    /// not resolve to nothing. A client pointing mid-statement, or at whitespace, gets the
    /// line it clicked on rather than a breakpoint that silently never fires — so both of
    /// line 36's statements stay armed.
    /// </summary>
    [SkippableFact]
    public async Task InlineBreakpointOnAColumnNoStatementStartsAt_FallsBackToTheWholeLine()
    {
        TestArtifacts.SkipIfMissing();

        // Column 2 is inside the indentation: no statement begins there.
        var (dap, bpResp) = await StartAndSetBreakpointAsync(SharedLine, 2);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(), bpResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var first = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SharedLine, first.GetProperty("body").GetProperty("line").GetInt32());
        Assert.Equal("0", (await ReadTopFrameLocalsAsync(dap))["Second"]);
        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        var second = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(30));
        Assert.Equal(SharedLine, second.GetProperty("body").GetProperty("line").GetInt32());
        Assert.Equal("2", (await ReadTopFrameLocalsAsync(dap))["Second"]);
    }
}
