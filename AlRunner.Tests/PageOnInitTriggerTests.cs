// PageOnInitTriggerTests — issue #4114. A RUNNER-MECHANISM test: what BC does is settled
// upstream by corpus codeunit 60488 "POI Tests"; this pins the runner's own dispatch of OnInit
// at page construction on the paths it drives, plus an Error() raised in OnInit reaching the
// test through the runner's construction catch sites.
using Xunit;

namespace AlRunner.Tests;

public sealed class PageOnInitTriggerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "PageOnInitTrigger");

    [Fact]
    public async Task OnInit_RunsOnceBeforeOnOpenPage_OnEveryOpenPath()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"every fixture test must pass. exit={r.ExitCode}\n{r}");

        r.AssertPassed("Codeunit71903.OpenEdit_RunsOnInitOnceBeforeOnOpenPage");
        r.AssertPassed("Codeunit71903.ActionBoundToOnInitGlobals_EnabledAndInvokeFollowThem");
        r.AssertPassed("Codeunit71903.Reopen_RunsOnInitAgain");
        r.AssertPassed("Codeunit71903.RunModal_RunsOnInitOnce");
        r.AssertPassed("Codeunit71903.PageRun_RunsOnInitOnce");
        r.AssertPassed("Codeunit71903.SetterBeforeRunModal_RunsAfterOnInit");
        r.AssertPassed("Codeunit71903.ErrorInOnInit_ReachesTheTest");
        r.AssertNoFailures();
    }
}
