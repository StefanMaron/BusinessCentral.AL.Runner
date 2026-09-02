// TestPageExtensionActionWithPartDispatchTests — issue #1995 (TestPage Invoke() of a
// pageextension-contributed action fails with "declares no OnAction trigger" when the SAME
// pageextension also adds a part() to the page layout).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR
// OWN dispatch path — RunnerPageInstance.GetOrCreateExtensionInstance — still constructs a
// working pageextension instance (and therefore still finds and runs its OnAction triggers)
// when that pageextension's AL-compiler-emitted constructor ALSO calls
// NavFormExtension.RegisterUIPart (emitted for a part() the extension adds to the layout).
//
// Root cause: GetOrCreateExtensionInstance used to invoke the extension's
// (ITreeObject, NavRecord) constructor with `_owner` (the TestPage's original caller,
// essentially never a NavForm) as the `parent` argument, then patched
// NavFormExtension.ParentObject to the real page form AFTERWARD via reflection, once the
// constructor had already returned. That is too late for any AL-emitted constructor code
// that touches ParentObject itself: a pageextension with a part() emits an
// InitializeComponent() override that calls ParentObject.RegisterUIPart(...) from INSIDE the
// constructor, which NREs on the still-null property and aborts construction — caching a
// permanently null instance for that extension id, so EVERY trigger the extension declares
// (not just ones near the part) reads as "extension not found" and every one of its actions
// misreports as declaring no OnAction trigger. The fix passes the page's own live form as the
// constructor's `parent` argument directly, so ParentObject is set correctly by
// NavFormExtension's own base constructor before any AL-emitted code runs.
//
// The BEHAVIORAL claim ("a pageextension action must still dispatch when the same
// pageextension also adds a part() to the layout, on real BC") is proven upstream against a
// live BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests PR for "TPXP
// Action Invoke Tests" (60729), per .claude/rules/bc-behavior-tests-go-upstream.md. This test
// exists so a regression in OUR OWN extension-instance construction fails loudly here,
// without needing the corpus's al-language submodule pin bumped (deliberately deferred to a
// separate centralized PR, per the orchestrator's process).
//
// RED/GREEN proof: reverting RunnerPageInstance.GetOrCreateExtensionInstance's
// `ctor.Invoke(new object?[] { _form, _record })` back to `ctor.Invoke(new object?[] { _owner,
// _record })` (the pre-fix shape) makes ActionOnPageExtensionWithPart_StillDispatches fail —
// Invoke() raises RunnerOutOfScopeException naming "testpage-action — the page declares no
// OnAction trigger for this action" for an action that plainly declares one, exactly the
// issue's own repro.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageExtensionActionWithPartDispatchTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageExtensionActionWithPartDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-pageext-part-action-dispatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    /// <summary>
    /// A list page with one action of its own AND an (empty) FactBoxes area, a CardPart page
    /// for a factbox, and a pageextension that adds BOTH a part() (via addfirst(FactBoxes),
    /// the same shape the issue's own repro used against a real Base App page that already
    /// declares one) AND two actions (unspaced and spaced names — #1968's discriminator is a
    /// different one, so both are covered to keep this test independent of that fix).
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "c1d2e3f4-5a6b-4780-8c9d-0e1f2a3b4c01",
          "name": "Runner Mechanism - TestPage PageExt Action With Part Dispatch",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62400, "to": 62409 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "PapRow.Table.al"), """
        table 62400 "Pap Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Descr; Text[50]) { }
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "PapList.Page.al"), """
        page 62401 "Pap List"
        {
            PageType = List;
            SourceTable = "Pap Row";
            ApplicationArea = All;
            UsageCategory = Lists;

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field("No."; Rec."No.")
                        {
                            ApplicationArea = All;
                        }
                    }
                }
                area(FactBoxes)
                {
                }
            }

            actions
            {
                area(Processing)
                {
                    action(DirectAction)
                    {
                        ApplicationArea = All;

                        trigger OnAction()
                        var
                            Row: Record "Pap Row";
                        begin
                            if not Row.Get('DIRECT') then begin
                                Row.Init();
                                Row."No." := 'DIRECT';
                                Row.Insert();
                            end;
                        end;
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "PapFactBox.Page.al"), """
        page 62402 "Pap FactBox"
        {
            PageType = CardPart;
            SourceTable = "Pap Row";
            Caption = 'Pap FactBox';

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.")
                    {
                        ApplicationArea = All;
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "PapListExt.PageExt.al"), """
        pageextension 62403 "Pap List Ext" extends "Pap List"
        {
            layout
            {
                addfirst(FactBoxes)
                {
                    part(PapFactBox; "Pap FactBox")
                    {
                        ApplicationArea = All;
                        SubPageLink = "No." = field("No.");
                    }
                }
            }

            actions
            {
                addlast(Processing)
                {
                    action(ExtActionWithPart)
                    {
                        ApplicationArea = All;

                        trigger OnAction()
                        var
                            Row: Record "Pap Row";
                        begin
                            if not Row.Get('EXT-WITH-PART') then begin
                                Row.Init();
                                Row."No." := 'EXT-WITH-PART';
                                Row.Insert();
                            end;
                        end;
                    }

                    action("Ext Action With Part Spaced")
                    {
                        ApplicationArea = All;

                        trigger OnAction()
                        var
                            Row: Record "Pap Row";
                        begin
                            if not Row.Get('EXT-WITH-PART-SPACED') then begin
                                Row.Init();
                                Row."No." := 'EXT-WITH-PART-SPACED';
                                Row.Insert();
                            end;
                        end;
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "PapTests.Codeunit.al"), """
        codeunit 62404 "Pap Tests"
        {
            Subtype = Test;

            [Test]
            procedure ActionOnPageExtensionWithPart_StillDispatches()
            var
                Row: Record "Pap Row";
                PapListPage: TestPage "Pap List";
            begin
                Row.DeleteAll();

                PapListPage.OpenEdit();
                PapListPage.ExtActionWithPart.Invoke();
                PapListPage.Close();

                if not Row.Get('EXT-WITH-PART') then
                    Error('Invoke() must have run the pageextension''s OnAction trigger, even though the same pageextension also adds a part() to the layout');
            end;

            [Test]
            procedure SpacedActionOnPageExtensionWithPart_StillDispatches()
            var
                Row: Record "Pap Row";
                PapListPage: TestPage "Pap List";
            begin
                Row.DeleteAll();

                PapListPage.OpenEdit();
                PapListPage."Ext Action With Part Spaced".Invoke();
                PapListPage.Close();

                if not Row.Get('EXT-WITH-PART-SPACED') then
                    Error('Invoke() must have run the pageextension''s spaced-name OnAction trigger, even though the same pageextension also adds a part() to the layout');
            end;

            [Test]
            procedure DirectActionOnTheBasePage_StillDispatches()
            var
                Row: Record "Pap Row";
                PapListPage: TestPage "Pap List";
            begin
                Row.DeleteAll();

                PapListPage.OpenEdit();
                PapListPage.DirectAction.Invoke();
                PapListPage.Close();

                if not Row.Get('DIRECT') then
                    Error('Invoke() must have run the page''s own OnAction trigger');
            end;
        }
        """);
    }

    private (string output, int exit) RunBundled()
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + $" \"{_root}\"");
        foreach (var a in ExtraPackageCacheArgs()) args.Append($" \"{a}\"");
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

    /// <summary>
    /// Positive, all three arms in one run: the part()-adding pageextension's unspaced
    /// action, its spaced-name action, and the base page's OWN action must all still
    /// dispatch. A stub that fell back to "does nothing" for the extension's actions would
    /// leave the first two rows unwritten while the third still passed — this asserts on the
    /// concrete effect of each, not merely "did not throw".
    /// </summary>
    [SkippableFact]
    public void ActionOnPageExtensionWithPart_StillDispatches()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62404.ActionOnPageExtensionWithPart_StillDispatches", output);
        Assert.Contains("PASS  Codeunit62404.SpacedActionOnPageExtensionWithPart_StillDispatches", output);
        Assert.Contains("PASS  Codeunit62404.DirectActionOnTheBasePage_StillDispatches", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
