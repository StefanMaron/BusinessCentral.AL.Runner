// TableTriggerMetadataTests — issue #3556.
//
// NCLMetaTable carries five Is<Trigger>Defined flags, and BC's own write path branches on them:
// NavRecord.ALInsert/ALModify/ALDelete/ALRename run the extension OnBefore<X> loop, the base
// table's own On<X> trigger and the extension On<X> loop INSIDE
// `if (runApplicationTrigger && metaTable.Is<X>TriggerDefined)`.
//
// What AL can observe is which triggers a write dispatches, and that is BC behaviour — it is
// pinned upstream by corpus codeunit 60433 (corpus PR #307), not here. This test pins the
// runner's own computation of the mask, which has no AL surface: AL_RUNNER_TABLE_TRIGGER_AUDIT=1
// prints one line per table and this asserts the exact set.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TableTriggerMetadataTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TableTriggerMetadata");

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
        psi.Environment["AL_RUNNER_TABLE_TRIGGER_AUDIT"] = "1";

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
    public void EveryTableTriggerFlagAnswersForTheTriggersItsTableAndTableextensionsDeclare()
    {
        var cacheDir = TestScratch.Dir("al-runner-ttm-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"the fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The defect this issue is about. Table 70740 declares NO trigger of its own, so
            // every bit here came from tableextension 70742 — which declares OnBeforeInsert
            // (Insert), OnAfterModify (Modify AND the separate OnAfterModify bit) and
            // OnBeforeDelete (Delete), and deliberately NOT the rename pair. An implementation
            // answering "all five bits" wholesale fails on the missing Rename; one that ignores
            // extensions, which is what the runner did before this fix, reports none of them.
            Assert.Contains(
                "[table-trigger-audit] table=70740 clr=Record70740 ext=TableExtension70742"
                + " triggers=Insert,Modify,Delete,OnAfterModify",
                stdout);

            // The base-table arm on its own: table 70741 declares OnRename and nothing else,
            // and has no tableextension. Rename alone, so a fix that reached the extension arm
            // by widening the base arm fails here.
            Assert.Contains(
                "[table-trigger-audit] table=70741 clr=Record70741 ext=- triggers=Rename",
                stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
