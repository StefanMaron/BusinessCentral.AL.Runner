// TestTimeoutFlagTests — RED->GREEN guard for issue #1648.
//
// v1 accepted `--test-timeout <seconds>` and used it to build the per-test failure
// message "Test exceeded {N}s timeout." v2 dropped the CLI override (hardcoded 60s in
// TestExecutor.DefaultTestTimeoutSeconds) and silently changed the message format to
// "TIMEOUT after {N}s" — an external caller that scales its own timeout budget off a
// configurable runner floor, or matches the old message text to detect a runner-side
// timeout (e.g. the AL mutation-testing tool LethAL), has no way to configure it and
// can no longer recognise a timeout by message text.
//
// Ghost-test trap avoided: the fixture's [Test] procedure runs `while true do;` — an AL
// infinite loop. A no-op fix (flag parsed but never wired to TestExecutor, or the old
// 60s default silently kept) makes the assertions below fail because either the process
// never times out inside the test's own 240s watchdog window, or the emitted message
// still reads "TIMEOUT after Ns" instead of "Test exceeded Ns timeout.".
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Used to be serialized with the other runner-subprocess integration tests
// (shared native BC engine state, SIGBUS flakes under xUnit's default
// parallelization) — see DefineFlagIntegrationTests; no longer is — #1809.
public sealed class TestTimeoutFlagTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestTimeoutFlagTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-test-timeout-flag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a minimal AL package: app.json (no dependencies, id range 62120..62129)
    /// and a test codeunit with one [Test] procedure that loops forever — it never
    /// returns on its own, so the ONLY way the test finishes is the runner's own
    /// per-test timeout firing.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b2c3d4e5-f6a7-8901-2345-67890abcdef1",
          "name": "Test Timeout Flag Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 62120, "to": 62129 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "TimeoutTest.Codeunit.al"), """
        codeunit 62121 "Test Timeout Flag Tests"
        {
            Subtype = Test;

            [Test]
            procedure NeverReturns()
            begin
                while true do;
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        // The fixture's [Test] never returns on its own; give the runner subprocess
        // enough headroom above the (short) --test-timeout to finish the whole run,
        // but well under a hang.
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// Positive: `--test-timeout 2` must cut the infinite-loop test off after ~2s and
    /// report it with the v1-compatible message text "Test exceeded 2s timeout.".
    /// Before the fix, the flag did not exist (unknown-option error) or, if merely
    /// wired but not to the message, would read "TIMEOUT after 2s" instead.
    /// </summary>
    [SkippableFact]
    public void TestTimeout_CutsOffInfiniteLoop_WithV1CompatibleMessage()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner("--test-timeout 2");

        Assert.Contains("NeverReturns", output);
        Assert.Contains("Test exceeded 2s timeout.", output);
        Assert.DoesNotContain("TIMEOUT after", output);
    }

    /// <summary>
    /// Negative: an invalid (non-positive, non-numeric) --test-timeout value must be
    /// rejected up front with exit code 2 and a message naming the flag, not silently
    /// accepted or interpreted as "no timeout".
    /// </summary>
    [Fact]
    public void TestTimeout_RejectsInvalidValue()
    {
        var (output, exit) = RunRunner("--test-timeout notanumber");

        Assert.Equal(2, exit);
        // Specific validation message, not just "unknown flag" (which would also exit 2
        // for an unrecognised --test-timeout and could make this assertion pass before
        // the flag is wired up at all).
        Assert.Contains("--test-timeout: 'notanumber' is not a positive integer", output);
    }
}
