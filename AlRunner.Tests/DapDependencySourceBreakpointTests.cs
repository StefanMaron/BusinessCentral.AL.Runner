// DapDependencySourceBreakpointTests — issue #4272.
//
// The DAP loop's EnsureSourceMap builds its map from `new[] { bundleDir }`, the one bundle the
// session was launched with. A sibling SOURCE dependency is compiled and executed by that same
// session and has no root in the map, so every line of its source resolves to nothing.
//
// What the client is told is the sharp part. With no entry for the file and no scan failure to
// report, DapUnverifiedReason falls through to "no executable AL statement on this line in this
// file" — a measured claim about AL, made about a file nobody read. That is the third state
// guards-need-a-third-state.md exists to keep apart, and it is why the control below asserts on
// the MESSAGE and not only on the flag.
//
// Fixture: Fixtures/CoverageDependencySource, shared with the coverage sites.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapDependencySourceBreakpointTests
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "CoverageDependencySource"));

    private static string ConsumingBundle => Path.Combine(FixtureRoot, "main");
    private static string DependencySource => Path.Combine(FixtureRoot, "dep", "CdsSubject.Codeunit.al");

    // Read from dep/CdsSubject.Codeunit.al, not inferred.
    private const int TwiceStatementLine = 9;   // exit(Value * 2);  — executed by the test
    private const int OpeningBraceLine = 6;     // {                 — carries no statement

    private const string NoStatementMessage = "no executable AL statement on this line in this file";

    /// <summary>
    /// Drives initialize / launch / setBreakpoints and returns the live client with the
    /// setBreakpoints response, matching the sequence a real client performs.
    /// </summary>
    private static async Task<(DapClient Dap, JsonElement BpResponse)> StartAndSetBreakpointsAsync(params int[] lines)
    {
        var dap = await DapClient.StartAsync(ConsumingBundle);
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
                source = new { path = DependencySource },
                breakpoints = lines.Select(l => new { line = l }).ToArray(),
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
    /// RED: verified:false, and the reason given is the false claim that line 9 carries no AL
    /// statement. GREEN: it verifies and execution pauses there — the dependency's source is a
    /// root of the session's map because the session compiled it.
    /// </summary>
    [SkippableFact]
    public async Task BreakpointInASiblingSourceDependency_VerifiesAndPauses()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointsAsync(TwiceStatementLine);
        await using var _ = dap;

        var bps = bpResp.GetProperty("body").GetProperty("breakpoints");
        Assert.Equal(1, bps.GetArrayLength());
        Assert.True(bps[0].GetProperty("verified").GetBoolean(),
            $"the dependency's line {TwiceStatementLine} was not verified — the DAP source map is "
            + $"built from the launched bundle alone (#4272): {bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.Equal(TwiceStatementLine, bps[0].GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // Verified is a claim about binding; pausing is what proves the binding is live.
        var stopped = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(180));
        Assert.Equal("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(TwiceStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        Assert.True(stResp.GetProperty("success").GetBoolean(), stResp.ToString());
        var frames = stResp.GetProperty("body").GetProperty("stackFrames");
        Assert.True(frames.GetArrayLength() >= 1, stResp.ToString());
        Assert.Equal(TwiceStatementLine, frames[0].GetProperty("line").GetInt32());
        Assert.Equal(
            Path.GetFullPath(DependencySource).Replace('\\', '/'),
            frames[0].GetProperty("source").GetProperty("path").GetString()!.Replace('\\', '/'));
    }

    /// <summary>
    /// THE control, and it is what the fix must NOT break: line 6 is the codeunit's opening
    /// brace. Before the fix it is unverified because the file has no root; after it, still
    /// unverified — but now because the AL really has no statement there, which is the same
    /// message meaning two different things. A fix that verifies every line of a dependency
    /// file rather than its statements fails here.
    /// </summary>
    [SkippableFact]
    public async Task NonStatementLineInThatSameDependency_StaysUnverified()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointsAsync(TwiceStatementLine, OpeningBraceLine);
        await using var _ = dap;

        var bps = bpResp.GetProperty("body").GetProperty("breakpoints");
        Assert.Equal(2, bps.GetArrayLength());

        // Asserted together so the pair is one measurement: same file, same request, one line
        // binds and the other does not.
        Assert.True(bps[0].GetProperty("verified").GetBoolean(),
            $"line {TwiceStatementLine} must verify: {bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.False(bps[1].GetProperty("verified").GetBoolean(),
            $"line {OpeningBraceLine} is an opening brace and must never bind: {bpResp}");
        Assert.Equal(NoStatementMessage, bps[1].GetProperty("message").GetString());
    }
}
