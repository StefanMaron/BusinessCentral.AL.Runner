// CodeunitMetadataVirtualTableTests — issue #2544.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR
// OWN population of the "CodeUnit Metadata" system virtual table (2000000137) works for
// codeunits declared by the bundle under test's own AL SOURCE, compiled fresh by this run
// and never shipped in a precompiled .app.
//
// Before the fix, table 2000000137 had no managed provider, so RecordPatches'
// GetDataAccessForTableCore fell through to the plain in-memory temp store and every read
// answered zero rows: Get() silently returned false and FindSet() raised. That made it the
// last missing member of a family the runner already implements — Table Metadata
// (2000000136), Page Metadata (2000000138), Report Metadata (2000000139).
//
// The fixture is shaped so a provider that answered every Get with a FIXED or BLANK row
// would fail: "CMV Bound" declares TableNo and nothing else, "CMV Single" declares
// SingleInstance and no TableNo, and the test codeunit itself declares Subtype = Test — so
// each of the three columns is asserted against a codeunit whose declaration makes it
// different from the others. The two negative tests (an unused id, and a filter selecting
// nothing) close the remaining hole.
//
// The BEHAVIORAL claim ("CodeUnit Metadata answers this shape on real BC") is proven
// upstream against a live BC service tier by "Test Codeunit Metadata Virt T" (60962) in
// StefanMaron/BusinessCentral.AL.Language.Tests, per
// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR
// OWN population pipeline fails loudly here, without needing the submodule pin bumped first.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeunitMetadataVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "CodeunitMetadataVirtualTable");

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
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s.");
        }
        // WaitForExit(int) returns as soon as the process exits and does NOT wait for the
        // async BeginOutputReadLine/BeginErrorReadLine callbacks to drain — only the
        // parameterless overload does. Without this the last stdout lines can still be in
        // flight when we read outSb, and an Assert.Contains on a line the runner definitely
        // printed fails intermittently, the more so the more loaded the machine is (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void CodeunitMetadata_SourceCompiledCodeunits_AllFixtureTestsPass()
    {
        var cacheDir = TestScratch.Dir("al-runner-cmv-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Positive: a fresh-source-compiled codeunit is found, and TableNo /
            // SingleInstance / Subtype are read off ITS OWN declaration.
            Assert.Contains(
                "PASS  Codeunit60764.CodeunitMetadata_SourceCompiledCodeunit_ColumnsComeFromItsDeclaration", stdout);
            // The mirror declaration: SingleInstance true, TableNo absent. Together with the
            // one above, this is what rules out a fixed row satisfying both.
            Assert.Contains(
                "PASS  Codeunit60764.CodeunitMetadata_SingleInstanceCodeunit_ReportsTrueAndNoTableNo", stdout);
            // Subtype is an OPTION column resolved against the live metatable's own option
            // string, not a hardcoded ordinal table.
            Assert.Contains(
                "PASS  Codeunit60764.CodeunitMetadata_TestCodeunit_ReportsSubtypeTest", stdout);
            // Negative: an id no codeunit uses still answers false, not a silent success.
            Assert.Contains(
                "PASS  Codeunit60764.CodeunitMetadata_UnknownCodeunitId_ReturnsFalse", stdout);
            // Negative: filtering discriminates — one row for a real id, none for an unused
            // one. A provider inserting one blank row would pass Get() and fail this.
            Assert.Contains(
                "PASS  Codeunit60764.CodeunitMetadata_FilterOnId_DiscriminatesBetweenRows", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
