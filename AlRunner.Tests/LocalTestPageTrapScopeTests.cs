// LocalTestPageTrapScopeTests — issue #4732. BcRuntime.RemoveLocalTestPageTraps
// (MethodScopePatches.cs) removes the outstanding Trap() of a disposing scope's local TestPage
// when that variable held the last reference to its page, and leaves the page itself alone. The
// BC behaviour is adjudicated upstream by corpus codeunit 67150 "Test Page Trap Scope Tests";
// this pins the runner's own mechanism, including the two boundaries it must not cross.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class LocalTestPageTrapScopeTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

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
          "platform": "1.0.0.0",
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
        """);
    }

    private (string output, int exit) RunBundled()
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + $" \"{_root}\"");
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" \"--package-cache\" \"{platformApps}\"");
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
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void AnUnconsumedTrap_EndsWithTheLastVariableHoldingItsPage()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        foreach (var name in new[]
                 {
                     "TrapOnALocal_EndsWithItsProcedure",
                     "TrapThroughAVarParameter_SurvivesTheCallee",
                     "TrapAssignedOutOfALocal_Survives",
                 })
            Assert.Contains("PASS  Codeunit64732." + name, output);
        Assert.DoesNotContain("FAIL", output);
    }
}
