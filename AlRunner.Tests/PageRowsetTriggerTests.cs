// PageRowsetTriggerTests — issue #3439.
//
// This is a RUNNER-MECHANISM test, not a claim anyone is asked to take on faith about BC.
//
// What BC does is settled upstream: corpus codeunits 60679 and 60680
// (StefanMaron/BusinessCentral.AL.Language.Tests#275) measure on a real service tier that a
// page's OnFindRecord/OnNextRecord decide which rows the client walks, and that Copy(Rec) onto
// a temporary record carries filters, current key and sort direction while keeping the buffer's
// own rows.
//
// This suite exists for the two things that verdict does NOT cover, both properties of the
// RUNNER's dispatch (MockTestPage's six navigation sites -> RunnerPageInstance.RaiseOnFindRecord
// / RaiseOnNextRecord) rather than of BC:
//
//   1. WHICH Which/Steps values the runner passes. The corpus cannot pin this and deliberately
//      does not: a real client anchors with '=><' or '=<' and walks with OnNextRecord(±1),
//      caching rows, so how many times it calls a trigger is a client-side detail. The runner
//      has no viewport, so it maps one navigation to one trigger call — '-', '+', +1, -1 — and
//      that mapping is what a regression would change first. docs/page-rowset-triggers.md has
//      the measured client trace and why the two differ.
//
//   2. THE NEGATIVE DIRECTION, which is the half that can break silently. OnFindRecord and
//      OnNextRecord are virtuals on NavForm whose BASE bodies are the platform find and the
//      platform step, so a declaration check that merely resolved the method name would fire on
//      every page in existence and route every ordinary page through a trigger it never wrote.
//      The fixture's plain page declares neither and asserts an EMPTY trace after a full walk.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageRowsetTriggerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "PageRowsetTriggers");

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

    [Fact]
    public void NavigationGoesThroughThePagesOwnTriggers_AndOnlyWhenItDeclaresThem()
    {
        var cacheDir = TestScratch.Dir("al-runner-prt-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"every fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Positive: the rowset a walk produces is the triggers', reached from both ends.
            Assert.Contains("PASS  Codeunit70645.TriggerPage_FirstAndNext_WalkTheRowsetTheTriggersServe", stdout);
            Assert.Contains("PASS  Codeunit70645.TriggerPage_LastAndPrevious_WalkTheRowsetTheTriggersServe", stdout);

            // Negative: a row the table holds and the triggers do not is refused. Without this,
            // an implementation reading the table passes every positive arm above.
            Assert.Contains("PASS  Codeunit70645.TriggerPage_GoToKey_RefusesARowTheTriggersDoNotServe", stdout);

            // The runner's own call sequence, which the corpus does not pin.
            Assert.Contains("PASS  Codeunit70645.TriggerPage_FirstAndNext_PassMinusAndPlusOne", stdout);
            Assert.Contains("PASS  Codeunit70645.TriggerPage_BeforeTheBufferIsActive_TheTriggersStillDecide", stdout);

            // The half that breaks silently: a page declaring neither trigger must raise neither.
            Assert.Contains("PASS  Codeunit70645.PlainPage_DeclaringNeitherTrigger_RaisesNeither", stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
