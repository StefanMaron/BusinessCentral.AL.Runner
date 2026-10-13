// DapBreakpointReplacementTests - #3526: setBreakpoints is the COMPLETE set for one source,
// replaced as a whole. DapMultiObjectFileTests already pins the empty list and the move between
// two objects of a file over TCP; these are the remaining acceptance cases, driven through a
// real session: a replacement carrying only unverified lines, another source left alone, a
// move between two procedures of one object, and both transports. Each asserts a stop where
// one is expected as well as the absence of the old one, so a replacement that disarms
// everything cannot pass.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapBreakpointReplacementTests
{
    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name));

    private static async Task InitializeAndLaunchAsync(DapClientBase dap)
    {
        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        var initEvents = new List<JsonElement>();
        var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
        Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
        if (!initEvents.Any(e => e.GetProperty("event").GetString() == "initialized"))
            await dap.ReadUntilEventAsync("initialized");

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(), $"launch failed: {launchResp}");
    }

    private static async Task<JsonElement> SetBreakpointsAsync(DapClientBase dap, string path, params int[] lines)
    {
        var seq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
        });
        var resp = await dap.ReadUntilResponseAsync(seq);
        Assert.True(resp.GetProperty("success").GetBoolean(), resp.ToString());
        return resp.GetProperty("body").GetProperty("breakpoints");
    }

    private static async Task ConfigurationDoneAsync(DapClientBase dap)
    {
        var seq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(seq);
    }

    private static async Task ContinueAsync(DapClientBase dap)
    {
        var seq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(seq);
    }

    /// <summary>Runs to exit and asserts no `stopped` event came before it.</summary>
    private static async Task AssertRunsToCleanExitAsync(DapClientBase dap, string because)
    {
        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.True(exited.GetProperty("body").GetProperty("exitCode").GetInt32() == 0, because);
    }

    /// <summary>
    /// Replacing a valid breakpoint with a list whose ONLY entry is unverified must still
    /// remove the old one: nothing in the new request resolves to a scope, so a handler that
    /// clears just the scopes the new request names clears nothing. Line 1 of the fixture is a
    /// doc comment, so it cannot bind.
    /// </summary>
    [SkippableFact]
    public async Task ReplacementWithOnlyAnUnverifiedLine_DisarmsThePreviousBreakpoint()
    {
        TestArtifacts.SkipIfMissing();
        var src = Path.Combine(Fixture("DapTwoObjects"), "TwoObjects.Codeunit.al");
        await using var dap = await DapClient.StartAsync(Fixture("DapTwoObjects"));
        await InitializeAndLaunchAsync(dap);

        var first = await SetBreakpointsAsync(dap, src, 32);
        Assert.True(first[0].GetProperty("verified").GetBoolean(), first.ToString());

        var second = await SetBreakpointsAsync(dap, src, 1);
        Assert.Equal(1, second.GetArrayLength());
        Assert.False(second[0].GetProperty("verified").GetBoolean(), second.ToString());

        await ConfigurationDoneAsync(dap);
        await AssertRunsToCleanExitAsync(dap, "the run stopped at, or failed after, a breakpoint the client had replaced");
    }

    /// <summary>
    /// Another source's breakpoints are not this request's to touch. The consumer's test and
    /// the dependency's procedure are two files of one session; removing the dependency's
    /// breakpoint must leave the consumer's one firing, and must itself stay removed.
    /// </summary>
    [SkippableFact]
    public async Task ReplacingOneSourcesBreakpoints_LeavesAnotherSourcesBreakpointArmed()
    {
        TestArtifacts.SkipIfMissing();
        var root = Fixture("CoverageDependencySource");
        var consumer = Path.Combine(root, "main", "CdsTests.Codeunit.al");
        var dependency = Path.Combine(root, "dep", "CdsSubject.Codeunit.al");
        const int consumerCallLine = 11;   // Actual := Subject.Twice(21);
        const int dependencyBodyLine = 9;  // exit(Value * 2);
        await using var dap = await DapClient.StartAsync(Path.Combine(root, "main"));
        await InitializeAndLaunchAsync(dap);

        var c = await SetBreakpointsAsync(dap, consumer, consumerCallLine);
        var d = await SetBreakpointsAsync(dap, dependency, dependencyBodyLine);
        Assert.True(c[0].GetProperty("verified").GetBoolean(), c.ToString());
        Assert.True(d[0].GetProperty("verified").GetBoolean(), d.ToString());

        await SetBreakpointsAsync(dap, dependency);   // the dependency's set is now empty

        await ConfigurationDoneAsync(dap);
        var stopped = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(180));
        Assert.Equal(consumerCallLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        await ContinueAsync(dap);
        await AssertRunsToCleanExitAsync(dap, "the consumer's run failed after its breakpoint");
    }

    /// <summary>
    /// stdio, and a move between two PROCEDURES of one object: Double's body (line 31) to the
    /// test method's own statement (line 23). Double runs first, so a line-31 breakpoint left
    /// armed would stop there before line 23 is ever reached.
    /// </summary>
    [SkippableFact]
    public async Task Stdio_MovingABreakpointBetweenProcedures_LeavesOnlyTheNewOneArmed()
    {
        TestArtifacts.SkipIfMissing();
        var src = Path.Combine(Fixture("DapStepping"), "DapSteppingTests.Codeunit.al");
        await using var dap = await DapStdioClient.StartAsync(Fixture("DapStepping"));
        await InitializeAndLaunchAsync(dap);

        var first = await SetBreakpointsAsync(dap, src, 31);
        Assert.True(first[0].GetProperty("verified").GetBoolean(), first.ToString());
        var second = await SetBreakpointsAsync(dap, src, 23);
        Assert.True(second[0].GetProperty("verified").GetBoolean(), second.ToString());

        await ConfigurationDoneAsync(dap);
        var stopped = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(180));
        Assert.Equal(23, stopped.GetProperty("body").GetProperty("line").GetInt32());

        await ContinueAsync(dap);
        await AssertRunsToCleanExitAsync(dap, "the run failed after the moved breakpoint");
    }

    /// <summary>stdio, and the empty list: the whole set for the source is nothing.</summary>
    [SkippableFact]
    public async Task Stdio_ReplacingWithAnEmptyList_DisarmsThePreviousBreakpoint()
    {
        TestArtifacts.SkipIfMissing();
        var src = Path.Combine(Fixture("DapBreakpoint"), "DapBreakpointTests.Codeunit.al");
        await using var dap = await DapStdioClient.StartAsync(Fixture("DapBreakpoint"));
        await InitializeAndLaunchAsync(dap);

        var first = await SetBreakpointsAsync(dap, src, 21);
        Assert.True(first[0].GetProperty("verified").GetBoolean(), first.ToString());
        var cleared = await SetBreakpointsAsync(dap, src);
        Assert.Equal(0, cleared.GetArrayLength());

        await ConfigurationDoneAsync(dap);
        await AssertRunsToCleanExitAsync(dap, "the run stopped at, or failed after, a removed breakpoint");
    }
}
