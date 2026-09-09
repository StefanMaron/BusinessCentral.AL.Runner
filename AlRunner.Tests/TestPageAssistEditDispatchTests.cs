// TestPageAssistEditDispatchTests — issues #2362 and #3642.
//
// A TestPage field's AssistEdit() never reached the control's OnAssistEdit trigger: both live
// ITestField implementations in MockTestPage carried `public void AssistEdit() { }`. #2362 is
// the base-page form of that (Microsoft's Tests-SINGLESERVER bucket: 8 tests on page 9807
// "User Card" whose declared UI handlers went unexecuted, so the test failed at teardown
// naming the handlers rather than the trigger). #3642 is the pageextension form — a control a
// `modify()` block contributes an OnAssistEdit to.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. The behavioural claim
// is upstream in StefanMaron/BusinessCentral.AL.Language.Tests PR #308, which adds four arms
// to codeunit 60514 covering both routes, targeting, and once-per-call. This file exists so a
// regression in OUR OWN dispatch fails loudly here without the al-language pin being bumped
// (the corpus PR has not merged yet, so the pin cannot move).
//
// WHY AN OBSERVABLE SIDE EFFECT AND NOT "did not throw". BC's own NavTestField.ALAssistEdit is
// `CheckError(() => testField.AssistEdit())` and ITestField.AssistEdit returns void, so the
// pre-fix no-op and a correct dispatch are the SAME observable unless the trigger leaves
// something behind. Every assertion below is therefore on a marker row the trigger writes.
// That is also why the absent-trigger arm asserts silence rather than an error: BC raises
// nothing for a control with no OnAssistEdit, so a refusal would invent an error real BC does
// not raise — see RunnerPageInstance.RaiseOnAssistEdit.
//
// RED/GREEN proof, measured on this branch with a probe bundle before the fix: both dispatch
// arms failed with an EMPTY trace (the trigger never ran, silently) and pass after it.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Spawns the runner as a subprocess, same convention as TestPageDrillDownDispatchTests, whose
// shape this follows deliberately: assist-edit is that test's sibling surface and the two
// should not diverge in how they are driven.
public sealed class TestPageAssistEditDispatchTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageAssistEditDispatchTests()
    {
        _root = TestScratch.Dir("al-runner-assistedit-dispatch");
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
    /// Three controls, so the two dispatch routes and the absent-trigger case are told apart
    /// in one run: BaseAssist carries the base page's OWN OnAssistEdit, ExtAssist carries none
    /// on the page and gets one from a pageextension's <c>modify()</c> block, and NoTrigger
    /// carries none anywhere.
    ///
    /// Each trigger appends its own tag to the row's Trace field rather than setting a flag,
    /// so an implementation that fires the wrong control's trigger — or fires every trigger it
    /// can find — produces a different string instead of the same "something happened".
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "c9e5a3b2-7f14-4d62-8b03-5a9e1c7d4f80",
          "name": "Runner Mechanism - TestPage AssistEdit Dispatch",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62710, "to": 62719 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "AEMechRow.Table.al"), """
        table 62710 "AE Mech Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Val; Text[50]) { }
                field(3; Extra; Text[50]) { }
                field(4; Trace; Text[250]) { }
            }

            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "AEMechCard.Page.al"), """
        page 62711 "AE Mech Card"
        {
            PageType = Card;
            SourceTable = "AE Mech Row";
            ApplicationArea = All;
            UsageCategory = Administration;

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.")
                    {
                        ApplicationArea = All;
                    }

                    field(BaseAssist; Rec.Val)
                    {
                        ApplicationArea = All;

                        trigger OnAssistEdit()
                        begin
                            Rec.Trace := Rec.Trace + 'base;';
                        end;
                    }

                    // No OnAssistEdit here: the pageextension below is the only possible
                    // source of an 'ext;' tag, which is what makes the extension arm about
                    // the extension route rather than a second reading of the base one.
                    field(ExtAssist; Rec.Extra)
                    {
                        ApplicationArea = All;
                    }

                    // No OnAssistEdit anywhere, on the page or in any extension.
                    field(NoTrigger; Rec."No.")
                    {
                        ApplicationArea = All;
                    }

                    field(Trace; Rec.Trace)
                    {
                        ApplicationArea = All;
                        Editable = false;
                    }
                }
            }
        }

        pageextension 62713 "AE Mech Card Ext" extends "AE Mech Card"
        {
            layout
            {
                modify(ExtAssist)
                {
                    trigger OnAssistEdit()
                    begin
                        Rec.Trace := Rec.Trace + 'ext;';
                    end;
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "AEMechTests.Codeunit.al"), """
        codeunit 62712 "AE Mech Tests"
        {
            Subtype = Test;

            // The base-page route (#2362), asserted on a concrete tag rather than on the
            // trigger merely not throwing.
            [Test]
            procedure AssistEditRunsTheBaseControlsOwnTrigger()
            var
                Row: Record "AE Mech Row";
                Card: TestPage "AE Mech Card";
                Trace: Text;
            begin
                Row.DeleteAll();

                Card.OpenNew();
                Card."No.".SetValue('ROW1');
                Card.BaseAssist.AssistEdit();
                Trace := Card.Trace.Value();
                Card.Close();

                if Trace <> 'base;' then
                    Error('AssistEdit() must run the control''s own OnAssistEdit; trace was ''%1''', Trace);
            end;

            // The pageextension modify() route (#3642). ExtAssist declares no OnAssistEdit on
            // the base page, so only the extension's block can produce this tag.
            [Test]
            procedure AssistEditRunsAModifyBlocksTrigger()
            var
                Row: Record "AE Mech Row";
                Card: TestPage "AE Mech Card";
                Trace: Text;
            begin
                Row.DeleteAll();

                Card.OpenNew();
                Card."No.".SetValue('ROW2');
                Card.ExtAssist.AssistEdit();
                Trace := Card.Trace.Value();
                Card.Close();

                if Trace <> 'ext;' then
                    Error('AssistEdit() must run a modify() block''s OnAssistEdit; trace was ''%1''', Trace);
            end;

            // Targeting: an implementation that raises every OnAssistEdit it can find on the
            // page passes both arms above and fails this one.
            [Test]
            procedure AssistEditIsRaisedOnlyForTheControlItWasCalledOn()
            var
                Row: Record "AE Mech Row";
                Card: TestPage "AE Mech Card";
                AfterBase: Text;
                AfterExt: Text;
            begin
                Row.DeleteAll();

                Card.OpenNew();
                Card."No.".SetValue('ROW3');
                Card.BaseAssist.AssistEdit();
                AfterBase := Card.Trace.Value();
                Card.ExtAssist.AssistEdit();
                AfterExt := Card.Trace.Value();
                Card.Close();

                if AfterBase <> 'base;' then
                    Error('assist-editing BaseAssist must raise only its own trigger; got ''%1''', AfterBase);
                if AfterExt <> 'base;ext;' then
                    Error('each AssistEdit() must raise exactly its own control''s trigger, in call order; got ''%1''', AfterExt);
            end;

            // Once per call, not once per page: an implementation that latches after the first
            // dispatch, or raises at page open, fails here and passes the arms above.
            [Test]
            procedure AssistEditRunsOncePerCall()
            var
                Row: Record "AE Mech Row";
                Card: TestPage "AE Mech Card";
                Trace: Text;
            begin
                Row.DeleteAll();

                Card.OpenNew();
                Card."No.".SetValue('ROW4');
                Card.BaseAssist.AssistEdit();
                Card.BaseAssist.AssistEdit();
                Trace := Card.Trace.Value();
                Card.Close();

                if Trace <> 'base;base;' then
                    Error('each AssistEdit() call must run the trigger again; trace was ''%1''', Trace);
            end;

            // The negative that keeps the fix honest in the OTHER direction. BC's ALAssistEdit
            // raises nothing for a control with no OnAssistEdit, so this must stay silent --
            // a refusal here would be inventing an error real BC does not raise, and would be
            // just as wrong as the no-op this fix removed.
            [Test]
            procedure AssistEditOnAControlWithNoTriggerIsSilent()
            var
                Row: Record "AE Mech Row";
                Card: TestPage "AE Mech Card";
                Trace: Text;
            begin
                Row.DeleteAll();

                Card.OpenNew();
                Card."No.".SetValue('ROW5');
                Card.NoTrigger.AssistEdit();
                Trace := Card.Trace.Value();
                Card.Close();

                if Trace <> '' then
                    Error('a control with no OnAssistEdit must leave no trace; got ''%1''', Trace);
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
    /// All five arms in one runner invocation: both dispatch routes produce their own concrete
    /// tag, neither leaks into the other, a second call runs the trigger again, and a control
    /// with no trigger stays silent rather than being refused.
    /// </summary>
    [SkippableFact]
    public void AssistEdit_DispatchesBothRoutesAndStaysSilentWhenAbsent()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62712.AssistEditRunsTheBaseControlsOwnTrigger", output);
        Assert.Contains("PASS  Codeunit62712.AssistEditRunsAModifyBlocksTrigger", output);
        Assert.Contains("PASS  Codeunit62712.AssistEditIsRaisedOnlyForTheControlItWasCalledOn", output);
        Assert.Contains("PASS  Codeunit62712.AssistEditRunsOncePerCall", output);
        Assert.Contains("PASS  Codeunit62712.AssistEditOnAControlWithNoTriggerIsSilent", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
