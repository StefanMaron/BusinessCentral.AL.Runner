// #2375: a re-exec parent runs the startup housekeeping and hands the fact to its immediate
// child. These pin the hand-off itself without touching the test process's environment (other
// tests spawn the runner and would inherit it); PhaseLogIntegrationTests pins that Main wires it.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// The CLEAR half of the hand-off, through the production wrapper: a `--jobs` worker is spawned
/// by the shadow child, after the hop, so it must not inherit the marker and must sweep again.
/// </summary>
public sealed class StartupHousekeepingJobsWorkerTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly string _root = TestScratch.Dir("housekeeping-jobs");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteBundle(string name, string id, int idFrom)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 9}} } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{idFrom}} "{{name}} Tests"
        {
            Subtype = Test;
            [Test]
            procedure Nothing()
            begin
            end;
        }
        """);
        return dir;
    }

    [SkippableFact]
    public void JobsWorkers_SpawnedAfterTheHop_RunHousekeepingAgain()
    {
        TestArtifacts.SkipIfMissing();

        var a = WriteBundle("HK Jobs A", "5d3c9a10-7e21-4b6f-9c0d-2375aa000001", 62150);
        var b = WriteBundle("HK Jobs B", "5d3c9a10-7e21-4b6f-9c0d-2375aa000002", 62160);
        var log = Path.Combine(_root, "phases.jsonl");

        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        args.Append($" --jobs 2 \"{a}\" \"{b}\"");

        var psi = new ProcessStartInfo("dotnet", args.ToString())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_PHASE_LOG"] = log;
        psi.Environment.Remove(ProgramSupport.StartupHousekeepingHandOffEnvVar);
        var output = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}. Output:\n{output}");

        var rows = File.ReadAllLines(log)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("kind").GetString()!.StartsWith("process", StringComparison.Ordinal))
            .OrderBy(e => e.GetProperty("start_ms").GetInt64())
            .ToList();
        var workers = rows.Where(r => r.GetProperty("bundles_in_process").GetInt32() == 1).ToList();
        var chain = rows.Where(r => r.GetProperty("bundles_in_process").GetInt32() != 1).ToList();

        // The outermost process and its shadow child: swept once, then handed off.
        Assert.True(chain.Count >= 2, $"no re-exec chain in the phase log:\n{string.Join("\n", rows)}");
        Assert.Equal(chain.Select((_, i) => i == 0).ToArray(),
            chain.Select(r => r.GetProperty("startup_housekeeping").GetBoolean()).ToArray());
        // Each worker sweeps again: the child cleared the marker before spawning them.
        Assert.Equal(2, workers.Count);
        Assert.All(workers, w => Assert.True(w.GetProperty("startup_housekeeping").GetBoolean(),
            $"a --jobs worker inherited the hand-off and skipped housekeeping: {w}"));
    }
}
