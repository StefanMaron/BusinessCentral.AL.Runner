using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2150 review follow-up (PR #2154) — the AL-diagnostic compile-failure guard added
/// for #2150 has to carve out AL1081 ("Unable to update report layout ... Could not find
/// file") because turning the guard on surfaced a PRE-EXISTING, unrelated runner bug
/// (#2151: the runner's Tier-3 source compile resolves a report's LayoutFile relative to
/// the app root instead of the .al file's own directory) across 6 al-language corpus
/// reports. A bare `d.Contains(": error AL1081:")` carve-out would tolerate EVERY AL1081,
/// including one naming a layout file that genuinely does not exist anywhere in the app —
/// exactly the silent-failure shape #2150 itself exists to remove, just moved to a
/// different error code. `IsKnownLayoutPathResolutionBug` (AlRunner/Program.cs) scopes the
/// carve-out to the SPECIFIC condition #2151 describes: the named file must actually exist
/// somewhere else under the app's own directory tree. This suite proves both directions of
/// that scoping directly against the CLI (not the C# helper in isolation), because the
/// claim is about end-to-end runner behaviour: does a real bundle compile/fail the way the
/// scoping intends.
/// </summary>
public class Al1081ToleranceScopingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string WriteApp(string suffix, string appId, int baseId)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-al1081-scoping-" + suffix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "AL1081 Scoping Test {{suffix}}",
          "publisher": "Repro2154",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Sample.Table.al"), $$"""
        table {{baseId}} "AL1081 Sample {{suffix}}"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
            }
            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);
        return root;
    }

    /// <summary>
    /// The exact shape of the al-language corpus bug #2151 tracks: the report .al file
    /// lives in a SUBDIRECTORY, LayoutFile is a relative reference resolved against that
    /// subdirectory in real BC, and the runner's app-root-relative resolution misses it —
    /// but the file genuinely exists on disk, just not where the runner looked. Must
    /// compile and run (exit 0), with the tolerance printed loudly and naming #2151 so a
    /// developer hitting this can tell it's the runner's fault, not theirs.
    /// </summary>
    [SkippableFact]
    public void LayoutFileResolvedAgainstWrongDirectory_FileExistsElsewhere_ToleratedLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var baseId = 62310;
        var root = WriteApp("tolerated", "f4444444-4444-4444-4444-444444444444", baseId);
        var handlersDir = Path.Combine(root, "handlers");
        Directory.CreateDirectory(handlersDir);

        // The layout genuinely exists — next to the report, exactly as AL's LayoutFile
        // semantics require — NOT at the app root the runner's RelativeFileSystem checks.
        File.WriteAllText(Path.Combine(handlersDir, "ScopingLayout.rdlc"), "<Report></Report>");
        File.WriteAllText(Path.Combine(handlersDir, "ScopingReport.Report.al"), $$"""
        report {{baseId + 1}} "AL1081 Report Tolerated"
        {
            UsageCategory = None;
            ProcessingOnly = false;
            DefaultRenderingLayout = ScopingLayout;

            dataset
            {
                dataitem(Sample; "AL1081 Sample tolerated")
                {
                    column(No; "No.") { }
                }
            }

            rendering
            {
                layout(ScopingLayout)
                {
                    Type = RDLC;
                    LayoutFile = './ScopingLayout.rdlc';
                }
            }
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.Equal(0, exitCode);
        Assert.Contains("AL1081-TOLERATED", output);
        Assert.Contains("2151", output);
        Assert.DoesNotContain("AL-DIAGNOSTIC-FAIL", output);
    }

    /// <summary>
    /// Negative direction — the scoping's whole reason to exist. A report naming a layout
    /// file that does not exist ANYWHERE under the app is a genuinely broken report, not
    /// #2151's bug, and must still fail loudly. A carve-out keyed on the bare "AL1081"
    /// error code alone (rather than "AND the file exists elsewhere") would wrongly pass
    /// this case — this test is what would have caught that regression.
    /// </summary>
    [SkippableFact]
    public void LayoutFileTrulyMissing_NotToleratedStillFailsCompile()
    {
        TestArtifacts.SkipIfMissing();

        var baseId = 62320;
        var root = WriteApp("missing", "f5555555-5555-5555-5555-555555555555", baseId);
        File.WriteAllText(Path.Combine(root, "MissingReport.Report.al"), $$"""
        report {{baseId + 1}} "AL1081 Report Missing"
        {
            UsageCategory = None;
            ProcessingOnly = false;
            DefaultRenderingLayout = MissingLayout;

            dataset
            {
                dataitem(Sample; "AL1081 Sample missing")
                {
                    column(No; "No.") { }
                }
            }

            rendering
            {
                layout(MissingLayout)
                {
                    Type = RDLC;
                    LayoutFile = './ThisFileGenuinelyDoesNotExistAnywhereInTheApp.rdlc';
                }
            }
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("AL1081", output);
        Assert.Contains("AL-DIAGNOSTIC-FAIL", output);
        Assert.DoesNotContain("AL1081-TOLERATED", output);
    }
}
