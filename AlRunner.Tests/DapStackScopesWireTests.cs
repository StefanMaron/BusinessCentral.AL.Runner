// DapStackScopesWireTests — #3906 and #3901 through a real --dap session.
//
// #3906: a client only requests `variables` for a positive variablesReference (DAP: 0 means "no
// children"), and the paused frame's id is 0. Every reader of locals in this suite used to pass
// whatever `scopes` returned straight back, which proves the adapter's own round trip and not
// that a client would have asked.
//
// #3901: a frame without a source must carry line/column 0. The assertion is a property of every
// frame in the response; a frame the source map does not know cannot be produced by any Dap*
// fixture (see AlDapWireTests), so AlDapWireTests drives that case directly.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapStackScopesWireTests
{
    private static readonly string SteppingFixture = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapStepping"));
    private const string SourceFileName = "DapSteppingTests.Codeunit.al";
    private const int DoubleFirstStatementLine = 31;   // Y := X * 2;   (see DapServerTests)
    private const int DoubleSecondStatementLine = 32;  // exit(Y);

    private static async Task<DapClient> StartAndPauseAsync(int line)
    {
        var dap = await DapClient.StartAsync(SteppingFixture);
        try
        {
            var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
            await dap.ReadUntilResponseAsync(initSeq);
            await dap.ReadUntilEventAsync("initialized");

            var launchSeq = dap.SendRequest("launch", new { });
            var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
            Assert.True(launchResp.GetProperty("success").GetBoolean(), launchResp.ToString());

            var bpSeq = dap.SendRequest("setBreakpoints", new
            {
                source = new { path = Path.Combine(SteppingFixture, SourceFileName) },
                breakpoints = new[] { new { line } },
            });
            var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
            Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(),
                bpResp.ToString());

            var cfgSeq = dap.SendRequest("configurationDone");
            await dap.ReadUntilResponseAsync(cfgSeq);
            await dap.ReadUntilEventAsync("stopped");
            return dap;
        }
        catch
        {
            await dap.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonElement> StackFramesAsync(DapClient dap)
    {
        var seq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var resp = await dap.ReadUntilResponseAsync(seq);
        Assert.True(resp.GetProperty("success").GetBoolean(), resp.ToString());
        return resp.GetProperty("body").GetProperty("stackFrames").Clone();
    }

    private static async Task<JsonElement> ScopesAsync(DapClient dap, int frameId)
    {
        var seq = dap.SendRequest("scopes", new { frameId });
        return await dap.ReadUntilResponseAsync(seq);
    }

    private static async Task<JsonElement> VariablesAsync(DapClient dap, int variablesReference)
    {
        var seq = dap.SendRequest("variables", new { variablesReference });
        return await dap.ReadUntilResponseAsync(seq);
    }

    private static HashSet<string> Names(JsonElement variablesResponse) =>
        variablesResponse.GetProperty("body").GetProperty("variables").EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()!).ToHashSet();

    /// <summary>
    /// Breakpoint inside Double (frame 0, locals X and Y) called from NestedCall (frame 1, local
    /// Result). Each frame's scope gets its own positive handle, and each handle yields ITS
    /// frame's locals and not the neighbour's.
    /// </summary>
    [SkippableFact]
    public async Task EachFramesLocalsScope_HasItsOwnPositiveReference_ThatYieldsThatFramesLocals()
    {
        TestArtifacts.SkipIfMissing();
        await using var dap = await StartAndPauseAsync(DoubleFirstStatementLine);

        var frames = await StackFramesAsync(dap);
        Assert.True(frames.GetArrayLength() >= 2, frames.ToString());
        var topId = frames[0].GetProperty("id").GetInt32();
        var callerId = frames[1].GetProperty("id").GetInt32();
        Assert.Equal(0, topId); // the id that used to be passed through as the reference

        var topRef = DapClientBase.LocalsReference(await ScopesAsync(dap, topId));
        var callerRef = DapClientBase.LocalsReference(await ScopesAsync(dap, callerId));
        Assert.NotEqual(topRef, callerRef);

        var top = Names(await VariablesAsync(dap, topRef));
        Assert.Contains("X", top);
        Assert.Contains("Y", top);
        Assert.DoesNotContain("Result", top);

        var caller = Names(await VariablesAsync(dap, callerRef));
        Assert.Contains("Result", caller);
        Assert.DoesNotContain("X", caller);
    }

    /// <summary>The negative direction: 0 is the "no children" value and never names a frame,
    /// and a frame id the stack does not have gets no scope at all.</summary>
    [SkippableFact]
    public async Task ZeroIsNotAVariablesReference_AndAnUnknownFrameHasNoScope()
    {
        TestArtifacts.SkipIfMissing();
        await using var dap = await StartAndPauseAsync(DoubleFirstStatementLine);

        var zero = await VariablesAsync(dap, 0);
        Assert.False(zero.GetProperty("success").GetBoolean(), zero.ToString());

        var unknown = await ScopesAsync(dap, 99);
        Assert.False(unknown.GetProperty("success").GetBoolean(), unknown.ToString());
    }

    /// <summary>DAP: a variablesReference must have been obtained in the current suspended state.
    /// A handle from the first stop must not resolve after the next one.</summary>
    [SkippableFact]
    public async Task AHandleFromAnEarlierStop_DoesNotResolveAfterTheNextStop()
    {
        TestArtifacts.SkipIfMissing();
        await using var dap = await StartAndPauseAsync(DoubleFirstStatementLine);

        var firstStopRef = DapClientBase.LocalsReference(await ScopesAsync(dap, 0));

        var nextSeq = dap.SendRequest("next", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(nextSeq);
        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(DoubleSecondStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stale = await VariablesAsync(dap, firstStopRef);
        Assert.False(stale.GetProperty("success").GetBoolean(), stale.ToString());

        var freshRef = DapClientBase.LocalsReference(await ScopesAsync(dap, 0));
        Assert.NotEqual(firstStopRef, freshRef);
        Assert.Contains("Y", Names(await VariablesAsync(dap, freshRef)));
    }

    /// <summary>#3901 as a property of a live response: whatever frames a stack holds, one with no
    /// source reports line and column 0, and one with a source reports a positive line.</summary>
    [SkippableFact]
    public async Task EveryFrame_EitherHasASourceAndALine_OrNeitherAndCoordinatesZero()
    {
        TestArtifacts.SkipIfMissing();
        await using var dap = await StartAndPauseAsync(DoubleFirstStatementLine);

        var frames = await StackFramesAsync(dap);
        Assert.True(frames.GetArrayLength() >= 2, frames.ToString());
        foreach (var f in frames.EnumerateArray())
        {
            if (f.TryGetProperty("source", out var source))
            {
                Assert.False(string.IsNullOrEmpty(source.GetProperty("path").GetString()), f.ToString());
                Assert.True(f.GetProperty("line").GetInt32() > 0, f.ToString());
            }
            else
            {
                Assert.Equal(0, f.GetProperty("line").GetInt32());
                Assert.Equal(0, f.GetProperty("column").GetInt32());
            }
        }
        // The control that keeps the loop above from passing on an empty or all-source-less set.
        Assert.Equal(DoubleFirstStatementLine, frames[0].GetProperty("line").GetInt32());
        Assert.True(frames[0].TryGetProperty("source", out _), frames.ToString());
    }
}
