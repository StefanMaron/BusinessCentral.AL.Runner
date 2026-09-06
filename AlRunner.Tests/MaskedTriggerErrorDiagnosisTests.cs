// MaskedTriggerErrorDiagnosisTests — issue #3189.
//
// This is a RUNNER-MECHANISM test. It asserts nothing new about Business Central.
//
// What BC does is already settled and is deliberately left alone: a page whose row-load trigger
// raises an AL error is torn down, and every later use of that TestPage variable reports BC's
// own "The TestPage is not open." — the raised error's own text never reaches AL. That was
// measured on 27.5 / 28.3 / 28.4 for #2656 and this suite's middle arm exists to keep the fix
// from changing it.
//
// What this suite proves is the runner's own REPORTING, which is where #3189 was:
//
//   1. a failure the runner converted names the error it converted, in the diagnosis line
//      beside the failure (positive);
//   2. AL still sees only BC's message — GetLastErrorText, inside the fixture, on the same
//      error (the guard that stops the fix from leaking the cause into AL);
//   3. a failure with nothing converted gets no diagnosis at all (negative);
//   4. MissingTestDataDiagnosis (#2240) can see THROUGH the mask — a page trigger that fails on
//      a table with no rows gets its [test-data] explanation, which it did not before, because
//      the replacement exception carries neither a table id nor a NavTestFieldException.
//
// 1 and 3 together are the claim for the [testpage] half. Without 3 the "fix" could be a line
// printed unconditionally, which would say nothing and would still make this file green.
//
// 4 EXISTS BECAUSE THE FIRST VERSION OF THIS SUITE DID NOT HAVE IT. Reverting
// MissingTestDataDiagnosis.TryNameTable's extra link left the suite green, because every arm
// then failed on a bare Error() carrying no typed table evidence — so the walk had nothing to
// find with or without the link. That is tdd.md's question answered "yes": it would have passed
// against an implementation that did nothing. MaskedSetupRecordError_CarriesBothExplanations
// fails on a missing setup record instead, and arm 1 now also asserts it gets NO [test-data]
// note, so the link cannot be "fixed" by firing on everything.
//
// WHY A FIXTURE AND A SUBPROCESS, rather than calling MaskedTriggerErrorDiagnosis.Explain with a
// hand-built exception: the thing that failed in #3189 was the WIRING. The cause was recorded
// correctly the whole time — it was written to an Exception.Data key that nothing read, and any
// unit test of the diagnosis in isolation would have passed against that. So the assertion has
// to be made on what a developer actually reads out of a real run.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class MaskedTriggerErrorDiagnosisTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "MaskedTriggerErrorDiagnosis");

    /// <summary>The AL error the fixture's part trigger raises. Unmistakable on purpose: an
    /// assertion that finds this string cannot be finding something else in the log.</summary>
    private const string PartTriggerError = "MTD-BOOM-70542 the part trigger refused this row";

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; the parameterless
        // overload does. See #2496.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    /// <summary>
    /// The reported block for one test: its header line plus every indented continuation line
    /// under it. The assertions are written against a single test's block rather than the whole
    /// log, because "the output carries no diagnosis" would otherwise be satisfied by the
    /// diagnosis simply landing under a different test.
    /// </summary>
    private static string BlockFor(string output, string testMethod)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var block = new StringBuilder();
        var inBlock = false;
        foreach (var line in lines)
        {
            var isHeader = line.StartsWith("FAIL ", StringComparison.Ordinal)
                        || line.StartsWith("PASS ", StringComparison.Ordinal)
                        || line.StartsWith("ERROR", StringComparison.Ordinal)
                        || line.StartsWith("SKIP ", StringComparison.Ordinal);
            if (isHeader)
            {
                if (inBlock) break;
                inBlock = line.Contains($".{testMethod} (", StringComparison.Ordinal);
                if (inBlock) block.AppendLine(line);
                continue;
            }
            if (inBlock) block.AppendLine(line);
        }
        Assert.True(block.Length > 0,
            $"no reported block for '{testMethod}' — the fixture did not run as expected. Full output:\n{output}");
        return block.ToString();
    }

    [Fact]
    public void ConvertedTriggerError_IsNamedInTheDiagnosis_AndNotShownToAl()
    {
        var cacheDir = TestScratch.Dir("al-runner-mtd-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            // Two of the three fixture tests fail by construction — the diagnosis only exists on
            // a reported failure, so there is nothing to observe in an all-green fixture.
            Assert.NotEqual(0, exit);

            // ── 1. positive: the converted error is named ─────────────────────────────────
            var masked = BlockFor(stdout, "MaskedPartTriggerError_IsReportedWithTheCauseNamed");

            // BC's own message is still what the failure REPORTS. The fix adds; it does not
            // replace.
            Assert.Contains("NavNCLDialogException: The TestPage is not open.",
                masked, StringComparison.Ordinal);

            // And the cause the runner had been discarding is now beside it, named with the
            // exception type as well as the text.
            Assert.Contains("[testpage]", masked, StringComparison.Ordinal);
            Assert.Contains(PartTriggerError, masked, StringComparison.Ordinal);

            // One line, because the bundle reporter keeps only line 1 of a message (#2261).
            var diagnosisLines = masked.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Contains("[testpage]", StringComparison.Ordinal)).ToList();
            Assert.Single(diagnosisLines);
            Assert.Contains(PartTriggerError, diagnosisLines[0], StringComparison.Ordinal);

            // A bare Error() names no table, so nothing widens into a missing-data claim. This
            // is the does-not-fire-spuriously half of arm 4 below: following one more link must
            // widen the SEARCH, never manufacture evidence.
            Assert.DoesNotContain("[test-data]", masked, StringComparison.Ordinal);

            // ── 2. the guard: AL saw only BC's message ───────────────────────────────────
            // Asserted inside the fixture, against GetLastErrorText on the very same error, so
            // this cannot pass by the cause merely being absent from the log.
            Assert.Contains("PASS  Codeunit70545.MaskedPartTriggerError_AlStillSeesOnlyBcsOwnMessage",
                stdout, StringComparison.Ordinal);

            // ── 4. the mask no longer hides evidence from MissingTestDataDiagnosis ───────
            // Same conversion, but the trigger failed on a missing setup record, so the
            // converted exception carries the AL table id that #2240's diagnosis needs. Both
            // explanations must be there: the converted cause, AND the empty table behind it.
            var setup = BlockFor(stdout, "MaskedSetupRecordError_CarriesBothExplanations");
            Assert.Contains("NavNCLDialogException: The TestPage is not open.",
                setup, StringComparison.Ordinal);
            Assert.Contains("[testpage]", setup, StringComparison.Ordinal);
            Assert.Contains("NavCSideRecordNotFoundException: The MTD Setup does not exist.",
                setup, StringComparison.Ordinal);

            // The half that is unreachable without TryNameTable following the converted-error
            // link. Table id as well as name, so this cannot be satisfied by the table name
            // merely appearing inside the [testpage] text.
            Assert.Contains("[test-data] 'MTD Setup' (table 70546) has no rows in this run",
                setup, StringComparison.Ordinal);

            // ── 3. negative: nothing converted, nothing said ─────────────────────────────
            var plain = BlockFor(stdout, "PlainFailure_IsReportedWithNoTestPageDiagnosis");
            Assert.Contains("MTD-PLAIN-70545 a failure with no page involved",
                plain, StringComparison.Ordinal);
            Assert.DoesNotContain("[testpage]", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("[test-data]", plain, StringComparison.Ordinal);

            Assert.DoesNotContain("suite errors", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
