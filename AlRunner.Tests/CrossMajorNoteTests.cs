// CrossMajorNoteTests — issue #2210.
//
// The auto-select default path (no --bc-version/--artifact-path given) used to print a
// "[bc] warning: ... (cross-major needs a matching runner build)" line whenever the target
// project's app.json (application/platform) declared a BC major different from the one this
// engine build actually resolves symbols against. Three problems, proven here:
//
//   1. It printed TWICE per run (once before the Ncl-shadow re-exec, once again in the
//      child that performs it) — reliably the loudest line in an otherwise all-green run.
//   2. The wording claimed a live compatibility hazard ("needs a matching runner build")
//      that #2210's own measurement did not find: the same AL source, exercising real
//      Base/System Application codeunits, produced identical pass/fail results whether run
//      against a matching-major engine or one major ahead of the app's declared floor (see
//      BcArtifacts.DescribeCrossMajorNote's doc comment for the exact measurement). BC's own
//      application/platform fields are minima, not pins, so this mismatch is not a
//      degradation to warn users away from.
//   3. Issue #2210's core question was "decide whether it should refuse or stop warning" —
//      a line firing on most runs of an affected project, which then pass, teaches users to
//      skim past everything the runner prints, INCLUDING the warnings that matter. A
//      condition with no measured divergence risk and no compatibility hazard does not
//      belong in a normal run's output at all, so it moved behind --verbose entirely: an
//      ordinary run says nothing about it, and anyone chasing exact-major parity can still
//      ask for it.
//
// This fixture (AlRunner.Tests/Fixtures/CrossMajorNote, application/platform "1.0.0.0")
// guarantees a mismatch on every host this suite runs on — no shipped runner engine will
// ever be built for BC major 1 — so the test is deterministic across every CI matrix leg
// (27.0-28.4) without depending on which BC version AlRunner.Tests itself was compiled
// against.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CrossMajorNoteTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string BundlePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "CrossMajorNote");

    private static (string StdOut, string StdErr, int Exit) RunRunner(bool verbose)
    {
        // Deliberately OMITS TestBuildConfig.BcVersionArg: the cross-major note only fires
        // on the auto-select default path (bcVersionArg == null in Program.cs) — an explicit
        // --bc-version bypasses that branch entirely, which would make this test vacuous.
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" \"{BundlePath}\"");
        if (verbose) args.Append(" --verbose");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s running the CrossMajorNote fixture.");
        }
        proc.WaitForExit();
        lock (outSb) lock (errSb) return (outSb.ToString(), errSb.ToString(), proc.ExitCode);
    }

    /// <summary>
    /// The core #2210 resolution: at DEFAULT verbosity, a declared-major mismatch must not
    /// stop the run (no refuse — the run must still execute and PASS its one test) AND must
    /// say nothing about the mismatch at all. This is the "stop warning on a run that then
    /// passes" half of the decision.
    /// </summary>
    [Fact]
    public void MismatchedDeclaredMajor_DefaultVerbosity_RunsAndPasses_SaysNothingAboutIt()
    {
        var (stdout, stderr, exit) = RunRunner(verbose: false);

        Assert.Equal(0, exit);
        Assert.Contains("PASS  Codeunit60950.CrossMajorNote_MismatchedDeclaredMajor_StillRunsAndPasses", stdout);
        Assert.Contains("pass:        1", stdout);
        Assert.Contains("fail:        0", stdout);

        // Nothing about the mismatch at all — neither the retired alarming wording nor the
        // new note. A condition with no measured divergence risk does not belong in an
        // ordinary run's output.
        Assert.DoesNotContain("cross major", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("declares BC major", stderr);
        Assert.DoesNotContain("needs a matching runner build", stderr);
    }

    /// <summary>
    /// Under --verbose, the note is still available (for anyone chasing exact-major
    /// parity) and must appear EXACTLY ONCE — not the pre-fix duplication (once before the
    /// Ncl-shadow re-exec, once again in the child that performs it) — worded as a measured,
    /// non-alarming fact rather than an unresolved compatibility warning.
    /// </summary>
    [Fact]
    public void MismatchedDeclaredMajor_Verbose_NotePrintedExactlyOnce()
    {
        var (stdout, stderr, exit) = RunRunner(verbose: true);

        Assert.Equal(0, exit);
        Assert.Contains("PASS  Codeunit60950.CrossMajorNote_MismatchedDeclaredMajor_StillRunsAndPasses", stdout);

        // Exactly once, not twice (the reported duplication) and not a stray third copy from
        // a stacked re-exec either.
        var occurrences = Regex.Matches(stderr, Regex.Escape("[bc] note: project app.json declares BC major 1")).Count;
        Assert.Equal(1, occurrences);

        // The old, inaccurate framing must be gone entirely — it claimed a hazard #2210's
        // measurement did not find.
        Assert.DoesNotContain("needs a matching runner build", stderr);
        Assert.DoesNotContain("cross-major needs a matching runner build", stderr);
    }
}
