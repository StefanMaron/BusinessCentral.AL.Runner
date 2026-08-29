using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2151 — a report declared in a subdirectory with a file-relative
/// <c>LayoutFile = './Foo.rdlc'</c> must compile clean: real BC resolves that path against
/// the DECLARING .al file's own directory, not the app root (confirmed by the al-language
/// corpus's own upstream CI — a real BC service tier — compiling six such reports clean).
/// The runner's Tier-3 source compile used to resolve it at the app root instead, which
/// does not exist there, and BC's compiler raised AL1081 ("Unable to update report layout
/// ... Could not find file") — previously carved out by a since-removed tolerance mechanism
/// (see PR #2154 / the deleted Al1081ToleranceScopingTests.cs). This suite proves the
/// runner now resolves the path CORRECTLY, end to end via the CLI, instead of merely
/// tolerating the diagnostic: no AL1081 anywhere in the output at all.
/// </summary>
public class ReportLayoutFileResolutionTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-report-layout-resolution-" + suffix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Report Layout Resolution Test {{suffix}}",
          "publisher": "Repro2151",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        return root;
    }

    /// <summary>
    /// Positive: the report lives in a subdirectory, its layout lives right beside it, and
    /// the report declares it with a file-relative "./" path — exactly the shape all six
    /// corpus reports use. Must compile AND run (exit 0), with NO AL1081 diagnostic and NO
    /// tolerance banner anywhere in the output — proving real resolution, not a carve-out.
    /// </summary>
    [SkippableFact]
    public void LayoutFileDeclaredFileRelative_ResolvesAgainstDeclaringDirectory_CompilesClean()
    {
        TestArtifacts.SkipIfMissing();

        var baseId = 62350;
        var root = WriteApp("resolved", "f6666666-6666-6666-6666-666666666666", baseId);
        var handlersDir = Path.Combine(root, "handlers");
        Directory.CreateDirectory(handlersDir);

        // The layout lives NEXT TO the report — nothing named "ResolvedLayout.rdlc" exists
        // at the app root, so a resolution that (still, wrongly) checked there first would
        // report AL1081 exactly like it used to.
        File.WriteAllText(Path.Combine(handlersDir, "ResolvedLayout.rdlc"), "<Report></Report>");
        File.WriteAllText(Path.Combine(handlersDir, "ResolvedReport.Report.al"), $$"""
        report {{baseId + 1}} "RLR Resolved Report"
        {
            UsageCategory = None;
            ProcessingOnly = false;
            DefaultRenderingLayout = ResolvedLayout;

            dataset
            {
                dataitem(Dummy; Integer)
                {
                    DataItemTableView = sorting(Number) where(Number = const(1));
                    column(N; Number) { }
                }
            }

            rendering
            {
                layout(ResolvedLayout)
                {
                    Type = RDLC;
                    LayoutFile = './ResolvedLayout.rdlc';
                }
            }
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("AL1081", output);
        Assert.DoesNotContain("AL1081-TOLERATED", output);
        Assert.DoesNotContain("AL-DIAGNOSTIC-FAIL", output);
    }

    /// <summary>
    /// Negative — the fix must not turn into a blanket AL1081 swallow. A report naming a
    /// layout file that does not exist ANYWHERE under the app is a genuinely broken report
    /// and must still fail loudly, with the real AL1081 surfacing (and, since the tolerance
    /// mechanism this issue removes no longer exists at all, no "AL1081-TOLERATED" banner
    /// can ever print again).
    /// </summary>
    [SkippableFact]
    public void LayoutFileTrulyMissing_StillFailsCompileLoudly()
    {
        TestArtifacts.SkipIfMissing();

        var baseId = 62360;
        var root = WriteApp("missing", "f7777777-7777-7777-7777-777777777777", baseId);
        File.WriteAllText(Path.Combine(root, "MissingReport.Report.al"), $$"""
        report {{baseId + 1}} "RLR Missing Report"
        {
            UsageCategory = None;
            ProcessingOnly = false;
            DefaultRenderingLayout = MissingLayout;

            dataset
            {
                dataitem(Dummy; Integer)
                {
                    DataItemTableView = sorting(Number) where(Number = const(1));
                    column(N; Number) { }
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
