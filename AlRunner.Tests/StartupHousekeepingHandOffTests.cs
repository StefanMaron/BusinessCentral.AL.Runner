// #2375: a re-exec parent runs the startup housekeeping and hands the fact to its immediate
// child. These pin the hand-off itself without touching the test process's environment (other
// tests spawn the runner and would inherit it); PhaseLogIntegrationTests pins that Main wires it.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public sealed class StartupHousekeepingHandOffTests
{
    private static (bool HandedOff, Dictionary<string, string?> After) Consume(Dictionary<string, string?> env)
    {
        var handedOff = ProgramSupport.ConsumeStartupHousekeepingHandOff(
            name => env.TryGetValue(name, out var v) ? v : null,
            name => env.Remove(name));
        return (handedOff, env);
    }

    [Fact]
    public void ChildOfAHandOff_SkipsHousekeeping_AndDoesNotPassItOn()
    {
        var psi = new ProcessStartInfo("dotnet");
        ProgramSupport.HandOffStartupHousekeeping(psi);
        var env = new Dictionary<string, string?>(psi.Environment);

        var (handedOff, after) = Consume(env);

        Assert.True(handedOff);
        // Cleared, so a process the child spawns later (a --jobs worker) sweeps again.
        Assert.False(after.ContainsKey(ProgramSupport.StartupHousekeepingHandOffEnvVar));
    }

    [Fact]
    public void AShadowDirRunByHand_StillRunsHousekeeping()
    {
        // AL_RUNNER_NCL_SHADOW_DONE=1 set by hand has no parent that swept for it.
        var (handedOff, _) = Consume(new Dictionary<string, string?> { ["AL_RUNNER_NCL_SHADOW_DONE"] = "1" });
        Assert.False(handedOff);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void AnythingButTheExactMarker_RunsHousekeeping(string? value)
    {
        var env = new Dictionary<string, string?>();
        if (value != null) env[ProgramSupport.StartupHousekeepingHandOffEnvVar] = value;
        Assert.False(Consume(env).HandedOff);
    }
}
