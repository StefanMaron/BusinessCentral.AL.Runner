// BackupReaderFailureReportingTests — issue #2779.
//
// WHAT WENT WRONG, MEASURED
//   The ms-bucket workflow's first run (Actions run 33967273260, Tests-ERM on BC
//   28.4.53241.54318) produced 0 tests, and the only thing the report said was:
//
//     Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1) for:
//       /home/runner/.cache/al-runner/bcbak/bcbak companies …/BusinessCentral-W1.bak
//
//   under a "=== Tests-ERM — COMPILE FAIL ===" header, with `compile-fail: 1`, `exec-fail: 0`
//   and `"classification": "compile/other"` in results.json.
//
//   The reader had in fact printed its reason to stderr:
//
//     error: block 116504 of MSDA region is neither mapped by the derived extent list nor
//     padding filler — backup layout differs from the derived model, refusing to guess
//
//   That text was appended as line 2 of the exception message, and every bundle-level reporter
//   keeps only line 1 (ExecFailure.Describe — a deliberate one-line contract that
//   ExecFailureTests pins). So the one sentence that diagnoses the failure was discarded, and
//   the label on what remained said "compile" about a run with zero AL compile errors.
//   Reproducing it took a manual download of the 932 MB backup and a hand-run of the reader.
//
// WHAT IS PROVED HERE
//   The unit tests below pin the message shape: the reader's own text on the FIRST line, for
//   both transports (spawn and serve). The end-to-end test spawns the real runner against a
//   fake reader that fails exactly the way the real one did, and asserts on what a reader of
//   the report actually sees — the header, the counters and results.json. Asserting on the
//   printed report rather than on an internal value is the point: an internal assertion cannot
//   see a diagnosis that never reaches the page, which is the whole defect.
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BackupReaderFailureReportingTests
{
    /// <summary>The real reader's real message for this failure, used verbatim so the test
    /// fails if the shape that actually occurred stops surviving.</summary>
    private const string RealReaderStderr =
        "error: block 116504 of MSDA region is neither mapped by the derived extent list nor "
        + "padding filler — backup layout differs from the derived model, refusing to guess";

    // ───────────────────────────────────────────── the message shape ──

    [Fact]
    public void Condense_PutsTheReadersDiagnosisOnOneLine()
    {
        var condensed = BackupReaderTool.Condense(RealReaderStderr + "\n");

        Assert.Equal(RealReaderStderr, condensed);
        Assert.DoesNotContain("\n", condensed, StringComparison.Ordinal);
    }

    [Fact]
    public void Condense_JoinsMultipleLinesRatherThanKeepingOnlyTheFirst()
    {
        var condensed = BackupReaderTool.Condense("first thing\nsecond thing\n\nthird thing");

        Assert.Contains("first thing", condensed, StringComparison.Ordinal);
        Assert.Contains("second thing", condensed, StringComparison.Ordinal);
        Assert.Contains("third thing", condensed, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", condensed, StringComparison.Ordinal);
    }

    /// <summary>Negative: a reader that says nothing must be reported as saying nothing, not as
    /// a message with a hole in it. "The binary died silently" is a different diagnosis from
    /// "the reader refused", and the report has to be able to tell them apart.</summary>
    [Fact]
    public void Condense_SaysSoWhenTheReaderPrintedNothing()
    {
        Assert.Contains("printed nothing", BackupReaderTool.Condense(""), StringComparison.Ordinal);
        Assert.Contains("printed nothing", BackupReaderTool.Condense(null), StringComparison.Ordinal);
        Assert.Contains("printed nothing", BackupReaderTool.Condense("   \n \n"), StringComparison.Ordinal);
    }

    /// <summary>A pathological reader must not turn one suite-error line into a wall of text.</summary>
    [Fact]
    public void Condense_CapsRunawayOutput_AndSaysHowMuchItDropped()
    {
        var condensed = BackupReaderTool.Condense(
            string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line {i}")));

        Assert.Contains("line 0", condensed, StringComparison.Ordinal);
        Assert.Contains("(+35 more line(s))", condensed, StringComparison.Ordinal);
        Assert.DoesNotContain("line 39", condensed, StringComparison.Ordinal);
    }

    /// <summary>The sibling transport: a serve-mode refusal carried the reader's text on line 2
    /// for exactly the same reason, so it was discarded exactly the same way.</summary>
    [Fact]
    public void AServeRefusalCarriesTheReadersTextOnTheFirstLine()
    {
        var ex = Assert.Throws<BackupReaderException>(() => BackupReaderServe.TranslateReadResponse(
            """{"id":2,"ok":false,"error":"no table matches 'No Such Table'"}""",
            "read No Such Table"));

        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains("no table matches 'No Such Table'", firstLine, StringComparison.Ordinal);
        Assert.Contains("read No Such Table", firstLine, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────── end to end ──

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>
    /// The whole failure, end to end, against a fake reader that fails the way the real one did:
    /// non-zero exit from `companies`, with the diagnosis on stderr.
    ///
    /// RED (pre-fix): stdout carries "COMPILE FAIL" and `compile-fail:1` / `exec-fail:   0`, and
    /// the reader's sentence appears nowhere in the output or in results.json.
    /// GREEN: "EXEC FAIL", `exec-fail:   1`, and the sentence in both.
    /// </summary>
    [SkippableFact]
    public void AReaderFailureIsReportedAsAnExecutionFailure_WithTheReadersOwnReason()
    {
        TestArtifacts.SkipIfMissing();
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake reader is a shell script");

        var root = TestScratch.Dir("al-runner-2779-reader-failure");
        var bundle = Path.Combine(root, "reader-failure-suite");
        Directory.CreateDirectory(bundle);

        File.WriteAllText(Path.Combine(bundle, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Reader Failure Reporting 2779",
          "publisher": "Repro2779",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62700, "to": 62709 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundle, "ReaderFailure.al"), """
        codeunit 62700 "Reader Failure Tests 2779"
        {
            Subtype = Test;

            [Test]
            procedure NeverReached()
            begin
                if 1 <> 1 then
                    Error('unreachable');
            end;
        }
        """);

        // A reader that fails the way the real one did. `--version` still answers, so the
        // failure is a refusal of THIS backup and not a broken install.
        var fakeReader = Path.Combine(root, "bcbak");
        File.WriteAllText(fakeReader, $"""
        #!/bin/sh
        if [ "$1" = "--version" ]; then echo "bcdb 0.0.0-fake"; exit 0; fi
        echo "{RealReaderStderr}" >&2
        exit 1
        """);
        File.SetUnixFileMode(fakeReader,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // Only its existence matters: the run never gets past `companies`.
        var backup = Path.Combine(root, "BusinessCentral-W1.bak");
        File.WriteAllBytes(backup, new byte[] { 0x54, 0x41, 0x50, 0x45 });

        var resultsJson = Path.Combine(root, "results.json");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
        args.Append(" \"--test-data=").Append(backup).Append('"');
        args.Append(" --test-data-company \"CRONUS International Ltd_\"");
        args.Append(" --out \"").Append(resultsJson).Append('"');

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment[BackupReaderTool.ExecutableEnvVar] = fakeReader;

        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output; lock (sb) output = sb.ToString();

        // The reader really was reached — without this the rest could pass for the wrong reason.
        Assert.Contains("block 116504 of MSDA region", output, StringComparison.Ordinal);

        // The label. This is the half that sent a human hunting for AL compile errors.
        Assert.Contains("— EXEC FAIL ===", output, StringComparison.Ordinal);
        Assert.DoesNotContain("— COMPILE FAIL ===", output, StringComparison.Ordinal);
        Assert.Contains("exec-fail:   1", output, StringComparison.Ordinal);
        Assert.Contains("compile-fail:0", output, StringComparison.Ordinal);

        // …and the same two claims in the machine-readable report the workflow uploads.
        var json = File.ReadAllText(resultsJson);
        Assert.Contains("\"kind\": \"execute\"", json, StringComparison.Ordinal);
        Assert.Contains("block 116504 of MSDA region", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\": \"compile\"", json, StringComparison.Ordinal);
    }
}
