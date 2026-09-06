// MalformedAlSyntaxDiagnosticTests — malformed AL must be reported as malformed AL.
//
// Issue #2949. A `keys` block whose key fields are separated by ';' instead of ','
// produced no AL diagnostic at all: the run died with BC's internal metadata-emit
// NullReferenceException, an AL0185 naming a DIFFERENT file (correct only because the
// table it references had been dropped), and a summary line telling the reader to
// "Re-run with --verbose for the AL diagnostics that identified them" — diagnostics
// that did not exist for that object under any verbosity.
//
// BC's parser was never silent about it. Measured with a standalone parse probe on
// 28.1.49838.53910, SyntaxTree.ParseObjectText returns four Error-severity diagnostics
// for the fixture table (AL0104 x3, AL0124), all anchored at line 14 where the ';' is.
// The runner discarded them: BcCompiler's emit-retry loop reassigns its `trees` array
// to the SURVIVING trees before parse diagnostics are collected, and the exclusion
// bookkeeping only ever consulted GetDeclarationDiagnostics(), which has nothing to say
// about a syntax error. So the one accurate account of the failure was thrown away and
// the reader was handed three signals that all point somewhere else.
//
// The bar these tests hold: a developer who writes malformed AL learns what is wrong
// with their AL, at DEFAULT verbosity, without being sent after diagnostics that do not
// exist. See .claude/rules/loud-failures.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class MalformedAlSyntaxDiagnosticTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "MalformedKeyList");

    private static (string Output, int Exit) RunRunner(string bundlePath, bool verbose = false)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        if (verbose) args.Append(" --verbose");
        args.Append($" \"{bundlePath}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
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

    /// <summary>
    /// The identifying half. The run must name the AL syntax error, its rule id and the
    /// file+line where the malformed separator sits — at default verbosity, which is what
    /// a developer and CI actually read.
    ///
    /// Not vacuous: before the fix this run also exited non-zero and also printed
    /// "EMIT-EXCLUDED", so a test asserting only "it failed" passed against the defect.
    /// What it could not do is produce AL0104 anywhere, at any verbosity.
    /// </summary>
    [SkippableFact]
    public void MalformedKeySeparator_NamesTheSyntaxErrorAndItsLine()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(FixturePath);

        Assert.True(exit != 0,
            $"malformed AL must fail the run. exit={exit}\n{output}");

        Assert.Contains("AL0104", output, StringComparison.Ordinal);
        Assert.Contains("Syntax error", output, StringComparison.Ordinal);

        // The offending file AND the offending line — "something is wrong somewhere" is
        // what the defect already produced. Line 14 is the `key(PK; "A"; "B")` line.
        Assert.Contains("MalformedKey.Table.al@14", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The misdirection half. AL0185 ("Table 'Malformed Key Row' is missing") is raised
    /// against the test codeunit, a file with nothing wrong in it, purely because the
    /// table was dropped. It may still appear — it is a true statement about the final
    /// compile round — but it must not be the FIRST thing the reader meets, or the reader
    /// goes and edits the wrong file. That is exactly the round trip #2949 records.
    /// </summary>
    [SkippableFact]
    public void MalformedKeySeparator_RealCauseIsReportedBeforeTheDownstreamError()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner(FixturePath);

        var syntaxAt = output.IndexOf("AL0104", StringComparison.Ordinal);
        var downstreamAt = output.IndexOf("AL0185", StringComparison.Ordinal);

        Assert.True(syntaxAt >= 0, $"the syntax error must be reported at all\n{output}");
        Assert.True(downstreamAt < 0 || syntaxAt < downstreamAt,
            $"the AL syntax error (offset {syntaxAt}) must be reported before the downstream "
            + $"AL0185 in the innocent file (offset {downstreamAt})\n{output}");
    }

    /// <summary>
    /// The advice half. The EMIT-EXCLUDED line used to end "Re-run with --verbose for the
    /// AL diagnostics that identified them" unconditionally — printed even when the run
    /// ALREADY had --verbose, and even when no diagnostic existed to be found. Advice that
    /// leads nowhere is worse than no advice: it reads as "there is more, go get it".
    ///
    /// Now the diagnostics accompany the failure, so the sentence must not be there. Held
    /// in both directions: default verbosity here, --verbose below.
    /// </summary>
    [SkippableFact]
    public void MalformedKeySeparator_DoesNotSendTheReaderAfterDiagnosticsItAlreadyPrinted()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner(FixturePath);

        Assert.Contains("AL0104", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-run with --verbose", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same claim under --verbose, where the old message was flatly self-contradictory:
    /// it told a reader who had already passed --verbose to pass --verbose.
    /// </summary>
    [SkippableFact]
    public void MalformedKeySeparator_Verbose_StillNamesTheSyntaxErrorAndGivesNoStaleAdvice()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(FixturePath, verbose: true);

        Assert.True(exit != 0, $"exit={exit}\n{output}");
        Assert.Contains("AL0104", output, StringComparison.Ordinal);
        Assert.Contains("MalformedKey.Table.al@14", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-run with --verbose", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction, and the thing that stops every assertion above from being
    /// satisfied by a runner that simply refuses this fixture for some other reason: with
    /// the separator corrected to ',' — the ONLY edit — the same bundle compiles, runs its
    /// test and passes, with no syntax diagnostic and no exclusion anywhere in the output.
    /// </summary>
    [SkippableFact]
    public void CorrectedKeySeparator_CompilesAndThePassingTestRuns()
    {
        TestArtifacts.SkipIfMissing();

        var tmp = TestScratch.Dir("al-runner-malformed-key-ok");
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in Directory.GetFiles(FixturePath))
                File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));

            var table = Path.Combine(tmp, "MalformedKey.Table.al");
            var fixedText = File.ReadAllText(table)
                .Replace("key(PK; \"A\"; \"B\")", "key(PK; \"A\", \"B\")", StringComparison.Ordinal);
            Assert.DoesNotContain("key(PK; \"A\"; \"B\")", fixedText, StringComparison.Ordinal);
            File.WriteAllText(table, fixedText);

            var (output, exit) = RunRunner(tmp);

            Assert.True(exit == 0, $"the corrected fixture must pass. exit={exit}\n{output}");
            Assert.Contains("InsertedRowIsCounted", output, StringComparison.Ordinal);
            Assert.DoesNotContain("AL0104", output, StringComparison.Ordinal);
            Assert.DoesNotContain("EMIT-EXCLUDED", output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
