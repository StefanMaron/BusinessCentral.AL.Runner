// LocalTestPageTrapScopeTests — issue #4732. BcRuntime.RemoveLocalTestPageTraps
// (MethodScopePatches.cs) removes the outstanding Trap() of a disposing scope's local TestPage
// when the scope's locals held every reference to its page (#4736: several locals may share
// one page), and leaves the page itself alone. The
// BC behaviour is adjudicated upstream by corpus codeunit 67150 "Test Page Trap Scope Tests";
// this pins the runner's own mechanism, including the two boundaries it must not cross.
using Xunit;

namespace AlRunner.Tests;

public sealed class LocalTestPageTrapScopeTests : IDisposable
{
    private readonly string _root;

    public LocalTestPageTrapScopeTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-trap-scope");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "4e0a7c31-9b2d-4f6e-8a15-c2d3e4f54732",
          "name": "Runner Mechanism - TestPage Trap Scope",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 64730, "to": 64739 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
        table 64730 "Tts Row"
        {
            DataClassification = CustomerContent;
            fields { field(1; "No."; Code[20]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 64731 "Tts Card"
        {
            PageType = Card;
            SourceTable = "Tts Row";
            ApplicationArea = All;
            UsageCategory = None;
            layout { area(Content) { field("No."; Rec."No.") { ApplicationArea = All; } } }
        }

        codeunit 64732 "Tts Tests"
        {
            Subtype = Test;

            var
                HandlerRan: Boolean;

            // The defect: the local variable's trap outlived it and captured this run.
            [Test]
            [HandlerFunctions('CardHandler')]
            procedure TrapOnALocal_EndsWithItsProcedure()
            begin
                TrapOnALocal();
                HandlerRan := false;
                Page.Run(Page::"Tts Card");
                if not HandlerRan then
                    Error('PageHandler did not run');
            end;

            // Boundary: a var parameter's trap belongs to the caller. No handler is declared, so a
            // removed trap would surface as an unhandled-UI error.
            [Test]
            procedure TrapThroughAVarParameter_SurvivesTheCallee()
            var
                Card: TestPage "Tts Card";
            begin
                TrapThroughVar(Card);
                Page.Run(Page::"Tts Card");
                Card.Close();
            end;

            // Boundary: a page still referenced by another variable keeps its trap.
            [Test]
            procedure TrapAssignedOutOfALocal_Survives()
            var
                Card: TestPage "Tts Card";
            begin
                TrapAndAssignOut(Card);
                Page.Run(Page::"Tts Card");
                Card.Close();
            end;

            // #4736: two locals share one page, so neither is its last reference on its own.
            [Test]
            [HandlerFunctions('CardHandler')]
            procedure TwoLocalsShareThePage_TrapEnds()
            begin
                TrapOnTwoLocals();
                HandlerRan := false;
                Page.Run(Page::"Tts Card");
                if not HandlerRan then
                    Error('PageHandler did not run');
            end;

            [Test]
            [HandlerFunctions('CardHandler')]
            procedure ThreeLocalsShareThePage_TrapEnds()
            begin
                TrapOnThreeLocals();
                HandlerRan := false;
                Page.Run(Page::"Tts Card");
                if not HandlerRan then
                    Error('PageHandler did not run');
            end;

            // Boundary: two locals share the page and one of them escapes, so the trap survives.
            [Test]
            procedure TwoLocalsShareThePageAndOneEscapes_TrapSurvives()
            var
                Card: TestPage "Tts Card";
            begin
                TrapOnTwoLocalsAndAssignOut(Card);
                Page.Run(Page::"Tts Card");
                Card.Close();
            end;

            local procedure TrapOnTwoLocals()
            var
                A: TestPage "Tts Card";
                B: TestPage "Tts Card";
            begin
                A.Trap();
                B := A;
            end;

            local procedure TrapOnThreeLocals()
            var
                A: TestPage "Tts Card";
                B: TestPage "Tts Card";
                C: TestPage "Tts Card";
            begin
                A.Trap();
                B := A;
                C := B;
            end;

            local procedure TrapOnTwoLocalsAndAssignOut(var Out: TestPage "Tts Card")
            var
                A: TestPage "Tts Card";
                B: TestPage "Tts Card";
            begin
                A.Trap();
                B := A;
                Out := B;
            end;

            local procedure TrapOnALocal()
            var
                Card: TestPage "Tts Card";
            begin
                Card.Trap();
            end;

            local procedure TrapThroughVar(var Card: TestPage "Tts Card")
            begin
                Card.Trap();
            end;

            local procedure TrapAndAssignOut(var Out: TestPage "Tts Card")
            var
                LocalPage: TestPage "Tts Card";
            begin
                LocalPage.Trap();
                Out := LocalPage;
            end;

            [PageHandler]
            procedure CardHandler(var Card: TestPage "Tts Card")
            begin
                HandlerRan := true;
            end;
        }

        // The disposed scope here is the test method's own: a trap left on a [Test]'s local must
        // not capture the next test's page run. Declaration order is the run order.
        codeunit 64733 "Tts Cross"
        {
            Subtype = Test;

            var
                HandlerRan: Boolean;

            [Test]
            procedure A_LeavesATrapInItsOwnLocal()
            var
                Card: TestPage "Tts Card";
            begin
                Card.Trap();
            end;

            [Test]
            [HandlerFunctions('CardHandler')]
            procedure B_ALaterTestReachesItsHandler()
            begin
                HandlerRan := false;
                Page.Run(Page::"Tts Card");
                if not HandlerRan then
                    Error('PageHandler did not run');
            end;

            [PageHandler]
            procedure CardHandler(var Card: TestPage "Tts Card")
            begin
                HandlerRan := true;
            end;
        }
        """);
    }

    [SkippableFact]
    public async Task AnUnconsumedTrap_EndsWithTheLastVariableHoldingItsPage()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var r = await SuiteServer.RunViaServer(_root);

        Assert.True(r.ExitCode == 0, $"Expected the bundle to pass; exit={r.ExitCode}\n{r}");
        foreach (var name in new[]
                 {
                     "TrapOnALocal_EndsWithItsProcedure",
                     "TrapThroughAVarParameter_SurvivesTheCallee",
                     "TrapAssignedOutOfALocal_Survives",
                     "TwoLocalsShareThePage_TrapEnds",
                     "ThreeLocalsShareThePage_TrapEnds",
                     "TwoLocalsShareThePageAndOneEscapes_TrapSurvives",
                 })
            r.AssertPassed("Codeunit64732." + name);
        foreach (var name in new[] { "A_LeavesATrapInItsOwnLocal", "B_ALaterTestReachesItsHandler" })
            r.AssertPassed("Codeunit64733." + name);
        r.AssertNoFailures();
    }
}
