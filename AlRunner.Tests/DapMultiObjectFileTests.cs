// DapMultiObjectFileTests — #3786: the DAP surface decodes a [SourceSpans] line as a FILE
// line, which is only true for the first object in a .al file.
//
// BC records a statement's line relative to the OWNING OBJECT's text. For the first object
// in a file the two numbering schemes coincide and nothing is visible; for a second object
// they differ by the line its text starts on. #3713 fixed the coverage consumers by
// carrying that start line in AlSourceLocationMap and adding it where a line is produced.
// DapBreakpointResolver and AlDapStackWalker were left.
//
// TWO defects, and measuring is what separated them. #3786 predicted that a line inside the
// FIRST object would falsely match a second-object statement at the same relative line. It
// does not: it comes back unverified too, because DapBreakpointResolver's path index held
// ONE (label,id) per path, so a file's second object evicted its first and there was nothing
// left to falsely match against. Recorded here because the disproven prediction is the more
// obvious story and a later reader will otherwise re-derive it.
//
//   * a line inside the second object never matched      -> the missing LineOffset
//   * a line inside the FIRST object never matched either -> the one-object-per-path index
//
// This class drives a real DAP session against Fixtures/DapTwoObjects, whose single .al file
// holds a helper codeunit (lines 10-16) and the test codeunit (text starting line 18). Each
// fact below is pinned to one of those two defects by mutation, so neither can be reverted
// without a red test.

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
    /// off-by-something that happens to land on one particular line. Counter is read as well
    /// as the line: at the FIRST statement nothing has been assigned yet, so 0 is what proves
    /// the pause is where the name says rather than one statement later.
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

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var frameId = stResp.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0]
            .GetProperty("variablesReference").GetInt32();
        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        var vars = varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
        Assert.True(vars.ContainsKey("Counter"), $"no Counter local reported: {varResp}");
        Assert.Equal("0", vars["Counter"]);
    }

    /// <summary>
    /// DAP's setBreakpoints contract: the request is the COMPLETE set for that source from
    /// then on. An empty list is how a client says "remove every breakpoint in this file",
    /// and it has to disarm one that a previous request armed.
    ///
    /// RED before the #3786 review's fix: the handler cleared only the scope types named by
    /// the NEW request, so an empty request named none, cleared nothing, and execution still
    /// stopped at a breakpoint the client had removed. It computed a full source path and
    /// never read it, which is the shape that defect left behind.
    /// </summary>
    [SkippableFact]
    public async Task SetBreakpointsWithAnEmptyList_DisarmsWhatThePreviousRequestArmed()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SecondObjectSecondStatementLine);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
            bpResp.ToString());

        // The same source, now with nothing in it.
        var clearSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = Array.Empty<object>(),
        });
        var clearResp = await dap.ReadUntilResponseAsync(clearSeq);
        Assert.True(clearResp.GetProperty("success").GetBoolean(), clearResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The same contract for a MOVE rather than a removal, which is the case the multi-object
    /// path index makes reachable: two objects of one file are now separately addressable, so
    /// replacing a breakpoint in the second object with one in the first must not leave the
    /// second armed. Both would fire otherwise, and the run would stop twice.
    /// </summary>
    [SkippableFact]
    public async Task MovingABreakpointBetweenTwoObjectsOfOneFile_LeavesOnlyTheNewOneArmed()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(SecondObjectSecondStatementLine);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
            bpResp.ToString());

        var moveSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = new[] { new { line = FirstObjectStatementLine } },
        });
        var moveResp = await dap.ReadUntilResponseAsync(moveSeq);
        Assert.True(moveResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
            moveResp.ToString());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // Exactly one stop, and in the object the LAST request named. Bump is called once, so
        // a second-object breakpoint left armed would add a stop at line 32 either before or
        // after this one.
        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(FirstObjectStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);

        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The first object must stay addressable, which is the half a fix that only ADDS an
    /// offset does not deliver. File line 14 is <c>exit(X + 1);</c> inside the FIRST object.
    ///
    /// RED before the fix: <c>verified: false</c> — not the false match #3786 predicted. The
    /// path index held one (label,id) per path, so whichever object was written last owned
    /// the file and the other could not be reached at all. Reverting only the index (leaving
    /// both offsets in place) turns this fact red again and leaves the other two green, which
    /// is what separates the two defects.
    ///
    /// Asserted through the stack rather than the line alone: paused inside <c>Bump</c>, the
    /// top frame is <c>Bump</c> and its CALLER is the second object's test method, whose own
    /// frame must report the file line of the call — the ancestor path in
    /// AlDapStackWalker.Walk, which an implementation offsetting only frame 0 would fail.
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
        Assert.True(frames.GetArrayLength() >= 2,
            $"expected the caller's frame below Bump's: {stResp}");

        var top = frames[0];
        Assert.Equal(FirstObjectStatementLine, top.GetProperty("line").GetInt32());
        var topName = top.GetProperty("name").GetString() ?? "";
        Assert.True(topName.Contains("Bump", StringComparison.OrdinalIgnoreCase),
            $"paused frame is '{topName}', not Bump — the requested first-object line resolved "
            + $"to a statement of the wrong object: {stResp}");

        // The ANCESTOR frame, and the reason this fact carries the stack walker's second code
        // path: Walk offsets every frame in its loop, and an implementation that offset only
        // frame 0 passes every other assertion in this class. The caller is the test method,
        // paused at its call to Bump — file line 32, in the SECOND object, so its frame is
        // where an un-offset ancestor would report 15.
        var caller = frames[1];
        var callerName = caller.GetProperty("name").GetString() ?? "";
        Assert.True(callerName.Contains("SecondObjectStatements", StringComparison.OrdinalIgnoreCase),
            $"frame below Bump is '{callerName}', not the calling test method: {stResp}");
        Assert.Equal(SecondObjectSecondStatementLine, caller.GetProperty("line").GetInt32());
    }
}
