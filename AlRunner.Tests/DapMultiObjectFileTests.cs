// DapMultiObjectFileTests — #3786: the DAP surface decodes a [SourceSpans] line as a FILE
// line, which is only true for the first object in a .al file.
//
// BC records a statement's line relative to the OWNING OBJECT's text. For the first object
// in a file the two numbering schemes coincide and nothing is visible. For a second object
// they differ by the line its text starts on, and two things go wrong at once:
//
//   * a breakpoint on a line inside the second object never matches, so it comes back
//     verified: false and never fires;
//   * a breakpoint on a line inside the FIRST object CAN match a statement of the second
//     whose relative line happens to equal it, so it fires in the wrong place.
//
// #3713 fixed the coverage consumers by carrying the object's start line in
// AlSourceLocationMap and adding it where a line is produced. DapBreakpointResolver and
// AlDapStackWalker were left, and this class is what makes that visible: it drives a real
// DAP session against Fixtures/DapTwoObjects, whose single .al file holds a helper codeunit
// (lines 10-16) and the test codeunit (text starting line 18).
//
// Both directions are here deliberately. The positive fact alone would pass an
// implementation that verified every requested line; the first-object fact alone would pass
// the unfixed code.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapMultiObjectFileTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapTwoObjects"));

    private const string SourceFileName = "TwoObjects.Codeunit.al";

    // Fixtures/DapTwoObjects/TwoObjects.Codeunit.al, asserted against exactly.
    // The SECOND object's text begins on line 18, so a statement of it decodes 17 too low
    // while nothing is added.
    private const int SecondObjectFirstStatementLine = 31;  // Counter := 1;
    private const int SecondObjectSecondStatementLine = 32; // Counter := Helper.Bump(Counter);
    private const int FirstObjectStatementLine = 14;        // exit(X + 1);  — inside Bump

    /// <summary>
    /// Drives initialize / launch / setBreakpoints / configurationDone and returns the
    /// setBreakpoints response together with the live client, so each fact below asserts on
    /// the same sequence a real DAP client performs.
    /// </summary>
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

    /// <summary>
    /// RED before the fix: <c>verified: false</c>. The requested file line 32 is compared
    /// against <c>AbsoluteFromLine(span)</c>, which for the second object in the file yields
    /// 15 — its line within that object's own text — so nothing matches and the breakpoint
    /// never fires. GREEN: it verifies, execution stops there, and the paused frame reports
    /// the file line the editor asked for rather than the relative one.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointInsideTheSecondObjectOfAFile_VerifiesAndPausesOnThatFileLine()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SecondObjectSecondStatementLine);
        await using var _ = dap;

        var bps = bpResp.GetProperty("body").GetProperty("breakpoints");
        Assert.Equal(1, bps.GetArrayLength());
        Assert.True(bps[0].GetProperty("verified").GetBoolean(),
            $"breakpoint at file line {SecondObjectSecondStatementLine} was not verified — the "
            + $"line was decoded relative to the object's text, not the file: {bpResp}");
        Assert.Equal(SecondObjectSecondStatementLine, bps[0].GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(SecondObjectSecondStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        // The stack walker is the second consumer #3786 names, and it is a separate code
        // path from the resolver: a frame could stop at the right place and still report the
        // relative line.
        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        Assert.True(stResp.GetProperty("success").GetBoolean(), stResp.ToString());
        var frames = stResp.GetProperty("body").GetProperty("stackFrames");
        Assert.True(frames.GetArrayLength() >= 1, stResp.ToString());
        Assert.Equal(SecondObjectSecondStatementLine, frames[0].GetProperty("line").GetInt32());

        // The pause is BEFORE the statement's own effect (AlDapSession's boundary), so
        // Counter is still the first statement's 1 — this is what proves the stop landed on
        // the statement we asked for rather than somewhere else that happens to be in range.
        var frameId = frames[0].GetProperty("id").GetInt32();
        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0]
            .GetProperty("variablesReference").GetInt32();
        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        var vars = varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
        Assert.True(vars.ContainsKey("Counter"), $"no Counter local reported: {varResp}");
        Assert.Equal("1", vars["Counter"]);
    }

    /// <summary>
    /// The first statement of the same object, so the fact above cannot pass by an
    /// off-by-something that happens to land on one particular line.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointOnTheSecondObjectsFirstStatement_StopsThereWithNoEffectApplied()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SecondObjectFirstStatementLine);
        await using var _ = dap;

        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
            bpResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SecondObjectFirstStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());
    }

    /// <summary>
    /// The false-positive direction, and the half a fix that only ADDS an offset could still
    /// get wrong. File line 14 is <c>exit(X + 1);</c> inside the FIRST object; relative to the
    /// second object's text, 14 is <c>Counter := 1;</c>. Unfixed, the requested line matches
    /// the second object's statement and execution stops in the test method instead of in
    /// <c>Bump</c> — a breakpoint that fires in the wrong function, which is worse than one
    /// that does not fire.
    ///
    /// Asserted through the frame's own function rather than only its line: a stop inside
    /// <c>Bump</c> has <c>Bump</c> on top of the stack, and the test method below it.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointOnAFirstObjectStatement_StopsInThatObject_NotTheSecondsSameRelativeLine()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(FirstObjectStatementLine);
        await using var _ = dap;

        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
            $"breakpoint at file line {FirstObjectStatementLine} (inside the first object) was "
            + $"not verified: {bpResp}");

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(FirstObjectStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var frames = stResp.GetProperty("body").GetProperty("stackFrames");
        var top = frames[0];
        Assert.Equal(FirstObjectStatementLine, top.GetProperty("line").GetInt32());
        var topName = top.GetProperty("name").GetString() ?? "";
        Assert.True(topName.Contains("Bump", StringComparison.OrdinalIgnoreCase),
            $"paused frame is '{topName}', not Bump — the requested first-object line matched a "
            + $"statement of the SECOND object at the same relative line: {stResp}");
    }
}
