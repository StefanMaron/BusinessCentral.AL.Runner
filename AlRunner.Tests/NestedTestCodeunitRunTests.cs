using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for #4827: the static <c>Codeunit.Run</c> of a <c>Subtype = Test</c>
/// codeunit reaches BC's own <c>NavTestCodeunit.DoRunAsync</c>, so a nested run is refused by
/// BC's <c>NavTestExecution.EnterTestCodeunit</c> rather than by anything the runner wrote.
///
/// The BC claim (the nested run errors, guarded or not, and runs no inner test) is measured
/// upstream by corpus codeunit 67566 (<c>TestCodeunitRunNestedTestCodeunit.al</c>). What this
/// pins is the wiring: the failure carries BC's own exception type,
/// <c>NavNCLTestCodeUnitNestedInvocationException</c>, which only BC's guard raises, and
/// <c>CodeunitPatches.NavCodeunit_RunCodeunit</c> is still the path a plain codeunit takes.
/// It also pins that a modal page and a non-modal page opened after the refused run still reach
/// their handlers:
/// BC's refused <c>DoRunAsync</c> releases the test page client, which
/// <c>RunnerModalDispatch.EnsureTestClientSession</c> rebuilds as BC's own getter does.
///
/// No asserterror and no Library Assert: each outer test lets the error reach the runner, so
/// the runner's FAIL line (exception type + message) is the assertion surface.
/// </summary>
public class NestedTestCodeunitRunTests
{
    private const string NestedException = "NavNCLTestCodeUnitNestedInvocationException";

    /// <summary>The first line of <paramref name="method"/>'s failure message (the CLI's line under its FAIL header), or null if it did not fail.</summary>
    private static string? FailureDetail(ServerRunResult r, string method)
    {
        var t = r.Tests.FirstOrDefault(t => t.Name.EndsWith("." + method, StringComparison.Ordinal));
        return t.Status is "fail" or "error" ? t.Message.Split('\n')[0].Trim() : null;
    }

    [SkippableFact]
    public async Task StaticRunOfTestCodeunit_FromATest_IsRefusedByBcsOwnGuard()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-nested-test-codeunit-4827");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4827000-0000-4000-8000-000000004827",
          "name": "NestedTestCodeunit4827",
          "publisher": "Repro4827",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 64827, "to": 64831 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Ntc.al"), """
        codeunit 64827 "NTC4827 Inner Tests"
        {
            Subtype = Test;

            [Test]
            procedure InnerTest_Runs()
            begin
            end;
        }

        codeunit 64828 "NTC4827 Plain"
        {
            trigger OnRun()
            begin
            end;
        }

        page 64830 "NTC4827 Dialog"
        {
            PageType = StandardDialog;
        }

        page 64831 "NTC4827 Card"
        {
            PageType = Card;
        }

        codeunit 64829 "NTC4827 Outer Tests"
        {
            Subtype = Test;

            var
                DialogHandled: Boolean;
                CardHandled: Boolean;

            [Test]
            procedure StaticRun_Unguarded()
            begin
                Codeunit.Run(Codeunit::"NTC4827 Inner Tests");
                Error('NTC4827 unguarded nested run was not refused');
            end;

            [Test]
            procedure StaticRun_Guarded()
            var
                Ok: Boolean;
            begin
                Ok := Codeunit.Run(Codeunit::"NTC4827 Inner Tests");
                Error('NTC4827 guarded nested run returned %1', Ok);
            end;

            // BC's NavTestCodeunit.DoRunAsync releases the test page client even when
            // EnterTestCodeunit refuses the run; the next modal page must still reach its handler.
            [Test]
            [HandlerFunctions('DialogHandler')]
            procedure ModalPage_AfterRefusedNestedRun_ReachesItsHandler()
            begin
                asserterror Codeunit.Run(Codeunit::"NTC4827 Inner Tests");
                Page.RunModal(Page::"NTC4827 Dialog");
                if not DialogHandled then
                    Error('NTC4827 modal handler did not run after the refused nested run');
            end;

            [ModalPageHandler]
            procedure DialogHandler(var Dialog: TestPage "NTC4827 Dialog")
            begin
                DialogHandled := true;
            end;

            [Test]
            [HandlerFunctions('CardHandler')]
            procedure Page_AfterRefusedNestedRun_ReachesItsPageHandler()
            begin
                asserterror Codeunit.Run(Codeunit::"NTC4827 Inner Tests");
                Page.Run(Page::"NTC4827 Card");
                if not CardHandled then
                    Error('NTC4827 page handler did not run after the refused nested run');
            end;

            [PageHandler]
            procedure CardHandler(var Card: TestPage "NTC4827 Card")
            begin
                CardHandled := true;
            end;

            [Test]
            procedure PlainRun_Succeeds()
            begin
                if not Codeunit.Run(Codeunit::"NTC4827 Plain") then
                    Error('NTC4827 plain Codeunit.Run returned false');
            end;
        }
        """);

        var r = await SuiteServer.RunViaServer(root);

        Assert.True(r.ExitCode == 1, $"Expected exactly the two nested runs to fail (exit 1); got exit {r.ExitCode}.\n{r}");

        foreach (var method in new[] { "StaticRun_Unguarded", "StaticRun_Guarded" })
        {
            var detail = FailureDetail(r, method);
            Assert.True(detail != null, $"{method} must fail: the nested run is refused.\n{r}");
            Assert.StartsWith(NestedException + ":", detail);
            Assert.Contains("Test codeunit 64827", detail);
        }

        Assert.Null(FailureDetail(r, "PlainRun_Succeeds"));
        Assert.Null(FailureDetail(r, "InnerTest_Runs"));
        Assert.Null(FailureDetail(r, "ModalPage_AfterRefusedNestedRun_ReachesItsHandler"));
        Assert.Null(FailureDetail(r, "Page_AfterRefusedNestedRun_ReachesItsPageHandler"));
        Assert.Equal(6, r.Total);
        Assert.DoesNotContain(r.Tests, t => t.Message.Contains("NTC4827 unguarded nested run was not refused"));
        Assert.DoesNotContain(r.Tests, t => t.Message.Contains("NTC4827 guarded nested run returned"));
    }
}
