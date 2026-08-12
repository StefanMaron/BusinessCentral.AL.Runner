// ExpectationManifestWiringTests — tests/expectations must actually reach the run.
//
// Issue #1734: AlRunner/Infrastructure/ExpectationManifest.cs implemented the whole
// classification table in docs/expectations.md and NOTHING ever called it. Every
// expectation entry was inert: an expect-oos test still failed the run, drift in either
// direction could never fire, and the documented escape hatch for corpus tests the
// runner cannot support did not exist.
//
// These tests spawn the real CLI against Fixtures/ExpectationsBundle (one codeunit,
// one method per classification path) plus Fixtures/ExpectationsManifest and pin the
// contract end-to-end:
//   - the reclassifying paths (pass-oos / pass-known-gap / pass-divergence / skipped)
//     reach the exit code,
//   - every drift direction fails the run with the documented diagnostics,
//   - a malformed manifest aborts startup loudly,
//   - without a manifest, behaviour is unchanged.
//
// Issue #1743 widened expect-oos to also recognise the Cecil-injected
// `out-of-scope: <api> — <reason>` message convention, and #1741 added
// expect-divergence. Both are covered here end-to-end; the negatives that keep the
// widened matcher honest live in ManifestDrift_EveryDirection_FailsTheRunLoudly.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
// [Collection("server-serial")] and no longer are — #1809.
public sealed class ExpectationManifestWiringTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string SuitePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "ExpectationsBundle", "suite");
    private static readonly string ManifestDir = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "ExpectationsManifest");
    private static readonly string MalformedManifestDir = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "ExpectationsManifestMalformed");

    private static (string Output, int Exit) RunRunner(string runnerArgs, string? workingDir = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(' ').Append(runnerArgs);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void AssertCount(string output, string label, int expected)
    {
        var m = Regex.Match(output, Regex.Escape(label) + @"\s*(\d+)");
        Assert.True(m.Success, $"summary must report a '{label}' count.\n{output}");
        Assert.True(int.Parse(m.Groups[1].Value) == expected,
            $"expected {label} {expected}, got {m.Groups[1].Value}.\n{output}");
    }

    /// <summary>
    /// The reclassifying paths in one run: a plain pass, a TYPED OOS throw, a
    /// Cecil-injected message-convention OOS throw (#1743), a declared known-gap
    /// failure, a declared intended divergence (#1741), and a declared skip. All must
    /// land the run at exit 0, with each reclassified count reported DISTINCTLY (a
    /// green run that got there via quarantined tests must not read as an unqualified
    /// green), and the skip-declared body must never execute.
    /// </summary>
    [SkippableFact]
    public void DeclaredExpectations_ReclassifyToGreen_AndReachTheExitCode()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(
            $"--expectations \"{ManifestDir}\" --test GreenPath \"{SuitePath}\"");

        // The skip entry must prevent INVOCATION, not just hide the result.
        Assert.DoesNotContain("SKIP-DECLARED TEST BODY RAN", output, StringComparison.Ordinal);

        // Each reclassified bucket is reported distinctly, per docs/expectations.md.
        // pass-oos is 2: the typed throw AND the Cecil-injected message convention.
        AssertCount(output, "pass-oos:", 2);
        AssertCount(output, "pass-known-gap:", 1);
        AssertCount(output, "pass-divergence:", 1);
        AssertCount(output, "skipped:", 1);
        AssertCount(output, "  fail:", 0);

        // The whole point of #1734: the reclassification reaches the exit code.
        Assert.True(exit == 0,
            $"declared expectations must reclassify to a green run. exit={exit}\n{output}");
    }

    /// <summary>
    /// Every drift direction in one run. Three of these are the load-bearing negatives
    /// for #1743: teaching expect-oos the message convention must NOT turn it into a
    /// matcher that says yes to everything, so a wrong reason, a one-character-short
    /// reason, and a failure carrying no out-of-scope signal at all must all still
    /// fail. Plus the two #1741 divergence directions. Manifest drift is loud.
    /// </summary>
    [SkippableFact]
    public void ManifestDrift_EveryDirection_FailsTheRunLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner($"--expectations \"{ManifestDir}\" \"{SuitePath}\"");

        // Direction 1a: expect-oos entry whose test passes → remove the entry.
        Assert.Contains("runner now supports this surface", output, StringComparison.Ordinal);
        // Direction 1b: known-gap entry whose test passes → remove the entry, close the issue.
        Assert.Contains("close the linked issue", output, StringComparison.Ordinal);
        // Direction 1c: divergence entry whose test passes → remove the entry.
        Assert.Contains("no longer diverges from BC", output, StringComparison.Ordinal);
        // Direction 2: undeclared OOS throw → add an entry. Fires for the typed throw
        // AND for the Cecil-injected message-convention throw.
        Assert.Contains("Unexpected out-of-scope: HttpClient.Get", output, StringComparison.Ordinal);
        Assert.Contains("Add an expect-oos entry", output, StringComparison.Ordinal);
        // Direction 3a: declared reason does not match the thrown reason.
        Assert.Contains("Expected OOS reason 'email-smtp' but runner threw reason 'external-http'",
            output, StringComparison.Ordinal);
        // Direction 3b: near-miss reason ('external-htt' is a prefix of 'external-http')
        // must not match — anchors are compared for equality, not containment.
        Assert.Contains("Expected OOS reason 'external-htt' but runner threw reason 'external-http'",
            output, StringComparison.Ordinal);
        // Direction 3c: an ordinary failure under an expect-oos entry is NOT absorbed
        // as out-of-scope just because the entry says so.
        Assert.Contains("no out-of-scope signal", output, StringComparison.Ordinal);
        // Direction 4: an OOS throw under an expect-divergence entry is the wrong mode.
        Assert.Contains("Declare it expect-oos", output, StringComparison.Ordinal);

        // The drift methods are the only failures; the green-path methods still
        // reclassify (drift must not disable classification for the rest of the run).
        AssertCount(output, "pass-oos:", 2);
        AssertCount(output, "pass-known-gap:", 1);
        AssertCount(output, "pass-divergence:", 1);
        AssertCount(output, "skipped:", 1);
        AssertCount(output, "  fail:", 9);   // every Drift_* method, and nothing else

        Assert.True(exit == 1,
            $"manifest drift must fail the run (exit 1 = test failures). exit={exit}\n{output}");
    }

    /// <summary>
    /// A malformed manifest (unknown Mode) must abort startup loudly, naming the file
    /// and the bad value — never run tests against a manifest it could not parse.
    /// </summary>
    [SkippableFact]
    public void MalformedManifest_AbortsStartupLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(
            $"--expectations \"{MalformedManifestDir}\" \"{SuitePath}\"");

        Assert.Contains("unknown Mode 'expect-magic'", output, StringComparison.Ordinal);
        Assert.True(exit == 2,
            $"a malformed manifest is a bad invocation and must exit 2 without running tests. exit={exit}\n{output}");
        // Startup aborted — nothing may have run. The loader's diagnostic quotes the
        // entry by AL object name, so probe for the CLR type name ("Codeunit60810"),
        // which only per-test run output produces.
        Assert.DoesNotContain("Codeunit60810", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction: with NO manifest (cwd without tests/expectations and no
    /// --expectations flag), behaviour is unchanged — an uncaught OOS throw is a plain
    /// FAIL without any drift diagnostic. Without this, the assertions above would
    /// still hold if classification ran unconditionally and rewrote every user-facing
    /// OOS failure into manifest advice.
    /// </summary>
    [SkippableFact]
    public void NoManifest_UnchangedBehaviour_OosIsAPlainFail()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(
            $"--test Drift_OosThrownButNoEntry \"{SuitePath}\"",
            workingDir: Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures"));

        Assert.DoesNotContain("Add an expect-oos entry", output, StringComparison.Ordinal);
        AssertCount(output, "  fail:", 1);
        Assert.True(exit == 1, $"an uncaught OOS throw stays a failing test. exit={exit}\n{output}");
    }
}
