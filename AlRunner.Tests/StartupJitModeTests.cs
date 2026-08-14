// StartupJitModeTests — pins the two JIT-mode decisions that dominate runner startup.
//
// Both were installed to protect JmpHook and both outlived it:
//
//   * AlRunner.csproj set <TieredCompilation>false</TieredCompilation> because "JMP-hook
//     patches written at tier-0 addresses are overwritten when .NET promotes hot methods
//     to tier-1". Cecil patches live in the IL, so every tier compiles the patched body —
//     promotion cannot undo them.
//   * Program.cs re-exec'd the whole process with DOTNET_ReadyToRun=0 so "hooks fire
//     deterministically". JmpHook.ComputeDisabled() now hard-returns true and a real run
//     reports "STARTUP-READY: 0 hooks applied", so there is no hook left to bypass. BC's
//     service-tier DLLs are IL-only anyway (machine=0x14c, zero R2R native bytes), so the
//     flag never protected Ncl — it only suppressed the FRAMEWORK's precompiled code.
//
// Together they made ~93% of the 9,264 methods a one-test run compiles go through the JIT
// at FULL optimisation, single-threaded, before the first test executes. Measured on a
// 4-vCPU box, one cached test: 9.50s -> 6.97s (-26.7%) warm, 14.4s -> 10.8s cold, and the
// 2076-test corpus run went 156.0s -> 133.7s. Fail-set identical (2076/2076) in every
// configuration.
//
// These assertions are behavioural on purpose. A test that only read AlRunner.csproj would
// pass against a runner whose runtimeconfig still disabled tiering for some other reason.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class StartupJitModeTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    private readonly string _scratch;

    public StartupJitModeTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "al-runner-jitmode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// The runner must not spawn a second OS process just to set DOTNET_ReadyToRun=0.
    ///
    /// Positive half: the run still passes 1/1, so this cannot go green by breaking
    /// execution. Negative half: the `[r2r] re-execing` marker — which the removed branch
    /// printed on EVERY invocation that did not already have the variable set — must be
    /// absent. Asserting on the marker rather than a process count keeps it independent of
    /// the ncl-cecil cache state, which legitimately adds its own labelled re-exec when cold.
    /// </summary>
    [SkippableFact]
    public void Runner_DoesNotReexecToDisableReadyToRun()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = Spawn($"{TestBuildConfig.BcVersionArg} \"{Fixture}\"", jitLogPath: null);

        Assert.DoesNotContain("[r2r] re-exec", output);
        Assert.DoesNotContain("DOTNET_ReadyToRun=0", output);
        Assert.Equal(0, exit);
        // Proves the run actually executed the fixture rather than exiting early: the
        // bundle has exactly one test and it must pass.
        Assert.Contains("pass:        1", output);
        Assert.Contains("fail:        0", output);
    }

    /// <summary>
    /// The runner process must JIT at tier-0, not full-opt.
    ///
    /// DOTNET_JitDisasmSummary emits one line per compiled method carrying its tier. The
    /// two modes are cleanly separable in that output, measured on the same fixture:
    ///
    ///   tiering OFF -> 8662 of 9264 methods "[FullOpts" (93.5%), and ZERO "[Tier0"
    ///                  (the 601 "[Tier-0 switched MinOpts" entries are a different label —
    ///                  note the hyphen — and appear in both modes)
    ///   tiering ON  -> 15082 "[Tier0" + 4508 "[Instrumented Tier0" of 20906, 142 FullOpts
    ///
    /// So "at least one thousand [Tier0 entries" cannot be satisfied by the disabled mode at
    /// all, and the FullOpts ceiling catches a partial regression that still admitted some
    /// tier-0 code.
    /// </summary>
    [SkippableFact]
    public void Runner_JitsAtTier0_NotFullOpts()
    {
        TestArtifacts.SkipIfMissing();

        var jitLog = Path.Combine(_scratch, "jit.txt");
        var (output, exit) = Spawn($"{TestBuildConfig.BcVersionArg} --quiet \"{Fixture}\"", jitLog);
        Assert.Equal(0, exit);

        // The JIT summary knob is a runtime diagnostic, not runner behaviour: if the host
        // runtime does not honour it there is nothing to assert and failing would blame the
        // runner for the runtime. Skipping is loud about which check was lost.
        TestArtifacts.SkipIf(!File.Exists(jitLog) || new FileInfo(jitLog).Length == 0,
            "DOTNET_JitDisasmSummary produced no output on this runtime — tier attribution unavailable");

        var lines = File.ReadAllLines(jitLog).Where(l => l.Contains("JIT compiled")).ToList();
        Assert.NotEmpty(lines);

        var tier0 = lines.Count(l => l.Contains("[Tier0") || l.Contains("[Instrumented Tier0"));
        var fullOpts = lines.Count(l => l.Contains("[FullOpts"));

        Assert.True(tier0 >= 1000,
            $"expected tier-0 JIT to dominate startup, saw {tier0} tier-0 of {lines.Count} compiled " +
            $"methods ({fullOpts} FullOpts) — tiered compilation looks disabled.\n{output}");
        Assert.True(fullOpts * 5 < lines.Count,
            $"expected FullOpts to be a small minority under tiering, saw {fullOpts} of {lines.Count}");
    }

    /// <summary>
    /// The emitted runtimeconfig must not carry the disable switch. This is the exact
    /// artifact the CLR reads, so re-adding &lt;TieredCompilation&gt;false&lt;/TieredCompilation&gt;
    /// to AlRunner.csproj fails here with a message naming the property — a faster and more
    /// specific signal than watching the tier histogram above invert.
    /// </summary>
    [Fact]
    public void RunnerRuntimeConfig_DoesNotDisableTieredCompilation()
    {
        var configPath = Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework,
            "al-runner.runtimeconfig.json");
        Assert.True(File.Exists(configPath), $"runner runtimeconfig not found at '{configPath}'");

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        if (doc.RootElement.TryGetProperty("runtimeOptions", out var opts)
            && opts.TryGetProperty("configProperties", out var props)
            && props.TryGetProperty("System.Runtime.TieredCompilation", out var tiered))
        {
            Assert.True(tiered.GetBoolean(),
                "al-runner.runtimeconfig.json sets System.Runtime.TieredCompilation=false — " +
                "the <TieredCompilation> property is back in AlRunner.csproj. It was a JmpHook-era " +
                "guard; Cecil patches live in the IL and survive tier promotion.");
        }
    }

    private (string Output, int Exit) Spawn(string runnerArgs, string? jitLogPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + " " + runnerArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Verbose so re-exec markers reach stdout/stderr: AlRunner/Log.cs installs a
        // FilteredWriter that drops `[Component]`-tagged lines at default verbosity.
        // Without this the absence asserted above would be indistinguishable from the
        // marker simply being filtered out — a vacuous pass.
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";
        // Never inherit an ambient override: the removed branch skipped itself when
        // DOTNET_ReadyToRun was already set, so an inherited value would make the
        // no-re-exec assertion pass without the code change.
        psi.Environment.Remove("DOTNET_ReadyToRun");
        psi.Environment.Remove("AL_RUNNER_ENABLE_R2R");
        psi.Environment.Remove("AL_RUNNER_R2R_REEXECED");
        // Same for the tier histogram: an inherited DOTNET_TieredCompilation=1 would make
        // the tier-0 assertion pass against a runtimeconfig that still disables tiering.
        psi.Environment.Remove("DOTNET_TieredCompilation");
        if (jitLogPath != null)
        {
            psi.Environment["DOTNET_JitDisasmSummary"] = "1";
            psi.Environment["DOTNET_JitStdOutFile"] = jitLogPath;
        }

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(300_000), "runner did not exit within 300s");
        p.WaitForExit();
        return (sb.ToString(), p.ExitCode);
    }
}
