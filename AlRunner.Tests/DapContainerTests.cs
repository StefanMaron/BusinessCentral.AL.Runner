// #5107 review: --dap launched on a folder that only contains apps must finish its session.
// RunDapLoop serves one module per session (one compiledTcs, one configurationDone release), so
// expanding the container into one run per app left the second run waiting forever — no `exited`,
// no `stopped`. --dap keeps the container as one bundle; its compile-rule gap is #5121.
// Fixture: Fixtures/CoverageDependencySource (dep, main, run), shared with the coverage sites.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapContainerTests
{
    private static readonly string Container = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "CoverageDependencySource"));

    // main/CdsTests.Codeunit.al line 11: `Actual := Subject.Twice(21);`, executed by the test.
    private const int TestStatementLine = 11;
    private static readonly TimeSpan EventBound = TimeSpan.FromSeconds(120);

    private static async Task<DapClient> LaunchAsync()
    {
        var dap = await DapClient.StartAsync(Container);
        try
        {
            var init = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
            await dap.ReadUntilResponseAsync(init);
            var launch = dap.SendRequest("launch", new { });
            var launchResp = await dap.ReadUntilResponseAsync(launch, timeout: TimeSpan.FromSeconds(180));
            Assert.True(launchResp.GetProperty("success").GetBoolean(),
                $"launch failed: {launchResp}\n--- stderr ---\n{dap.StdErr}");
            return dap;
        }
        catch
        {
            await dap.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonElement> ExpectEventAsync(DapClient dap, string name)
    {
        try { return await dap.ReadUntilEventAsync(name, EventBound); }
        catch (Exception ex)
        {
            Assert.Fail($"no '{name}' event within {EventBound.TotalSeconds}s: {ex.Message}\n--- stderr ---\n{dap.StdErr}");
            throw;
        }
    }

    [SkippableFact]
    public async Task Container_NoBreakpoints_ExitsWithZero()
    {
        TestArtifacts.SkipIfMissing();
        await using var dap = await LaunchAsync();
        await dap.ReadUntilResponseAsync(dap.SendRequest("configurationDone"));

        var exited = await ExpectEventAsync(dap, "exited");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    [SkippableFact]
    public async Task Container_BreakpointInTestApp_StopsThenExits()
    {
        TestArtifacts.SkipIfMissing();
        await using var dap = await LaunchAsync();
        var bp = await dap.ReadUntilResponseAsync(dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(Container, "main", "CdsTests.Codeunit.al") },
            breakpoints = new[] { new { line = TestStatementLine } },
        }));
        Assert.True(bp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(), bp.ToString());
        await dap.ReadUntilResponseAsync(dap.SendRequest("configurationDone"));

        var stopped = await ExpectEventAsync(dap, "stopped");
        Assert.Equal("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());

        await dap.ReadUntilResponseAsync(dap.SendRequest("continue", new { threadId = 1 }));
        var exited = await ExpectEventAsync(dap, "exited");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }
}
