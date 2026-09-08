// PageTriggerMetadataTests — issue #3447.
//
// NCLMetaForm carries twelve Is<Trigger>Defined flags, and BC's own runtime branches on them
// (NavForm.RaiseOnNextRecordAsync, RaiseOnFindRecordAsync, the OnNewRecord path,
// NavEventSubscriptionMetadata's OnAfterGetCurrRecord wiring). They are page METADATA, not an AL
// surface, and BC's consumers of them sit on dispatch the runner serves itself — so there is
// nothing a corpus test could observe. AL_RUNNER_PAGE_TRIGGER_AUDIT=1 prints what the runner
// computed, one line per page, and this asserts the exact set.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageTriggerMetadataTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "PageTriggerMetadata");

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
        psi.Environment["AL_RUNNER_PAGE_TRIGGER_AUDIT"] = "1";

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

    [Fact]
    public void EveryPageTriggerFlagAnswersForTheTriggersItsPageAndPageextensionsDeclare()
    {
        var cacheDir = TestScratch.Dir("al-runner-ptm-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"the fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The ten a page can declare besides the two rowset triggers, in BC's own enum
            // order — and NOT OnFindRecord/OnNextRecord, which this page does not declare.
            Assert.Contains(
                "[page-trigger-audit] page=70661 clr=Page70661 ext=- triggers="
                + "OnInit,OnOpenPage,OnClosePage,OnAfterGetRecord,OnNewRecord,OnInsertRecord,"
                + "OnModifyRecord,OnDeleteRecord,OnQueryClosePage,OnAfterGetCurrRecord",
                stdout);

            // The defect this issue is about: page 70662 declares nothing, and both of these
            // come from pageextension 70663. Two of the nine a pageextension may declare, so an
            // implementation answering true wholesale fails here.
            Assert.Contains(
                "[page-trigger-audit] page=70662 clr=Page70662 ext=PageExtension70663"
                + " triggers=OnOpenPage,OnAfterGetRecord",
                stdout);

            // The pair RunnerPageInstance.DeclaresRowsetTrigger now reads (#3439).
            Assert.Contains(
                "[page-trigger-audit] page=70664 clr=Page70664 ext=- triggers=OnFindRecord,OnNextRecord",
                stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
