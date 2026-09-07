using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #3436: BC's nine implicit PAGE trigger events
/// (<c>OnOpenPageEvent</c> … <c>OnQueryClosePageEvent</c>) never reached their subscribers.
///
/// It proves two of OUR OWN components, which is what makes it a mechanism test rather than
/// a duplicate of the upstream claim:
///
/// 1. <c>EventSubscriberPatches.InjectPageTriggerSubs</c> registers a page-scoped subscriber
///    into the page's own <c>NavEventScope</c> — before it, the subscriber was filed under
///    the registry for a manually-declared <c>[IntegrationEvent]</c> (#1794) that BC's
///    implicit-event path never consults, so nothing fired and nothing complained; and
/// 2. <c>RecordPatches.NCLMetaApplicationObject_get_ApplicationObjectClrType</c> resolves a
///    Page to <c>Page{id}</c>. It answered <c>Form{id}</c>, hence null for every page, and
///    <c>NavEventSubscription</c>'s ctor NREs on a null publisher CLR type inside
///    <c>GetScopeType</c> — with that alone unfixed, 115 of 115 page subscriptions failed to
///    build and the run still reported nothing.
///
/// The BEHAVIORAL claim (which page events real BC raises, in what order, with which Rec /
/// xRec values, and that <c>AllowModify := false</c> vetoes the write) is proven upstream
/// against a live BC service tier — <c>StefanMaron/BusinessCentral.AL.Language.Tests</c>#274,
/// merged, nine tests green on all eight cloud legs. This test exists so a regression in the
/// runner's own registration fails loudly here, without depending on the submodule pin having
/// moved (it has not: the intervening corpus commits need five other open runner PRs first —
/// see this PR's body).
///
/// No <c>"application"</c> in the fixture's app.json (.claude/rules/no-base-app-in-csharp-tests.md):
/// the AL raises its own <c>Error()</c> carrying the observed value, so the runner's PASS/FAIL
/// output is the assertion surface.
/// </summary>
public class PageTriggerEventDispatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
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

    [SkippableFact]
    public void PageTriggerEventSubscribers_AreRegisteredAndTheirVetoIsHonoured()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-page-trigger-events-3436");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3436000-0000-4000-8000-000000003436",
          "name": "PageTriggerEventDispatch3436",
          "publisher": "Repro3436",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63436, "to": 63439 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "PteObjects.al"), """
        table 63436 "PTE Row"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "Code"; Code[20]) { }
                field(10; "Value"; Text[50]) { }
            }

            keys { key(PK; "Code") { Clustered = true; } }
        }

        page 63437 "PTE Rows"
        {
            PageType = List;
            SourceTable = "PTE Row";
            ApplicationArea = All;
            UsageCategory = Lists;

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field("Code"; Rec."Code") { ApplicationArea = All; }
                        field("Value"; Rec."Value") { ApplicationArea = All; }
                    }
                }
            }

            actions
            {
                area(Processing)
                {
                    action(DirectSave)
                    {
                        ApplicationArea = All;
                        trigger OnAction()
                        begin
                            Rec.Validate("Value", 'DIRECT');
                            CurrPage.SaveRecord();
                        end;
                    }
                }
            }
        }

        codeunit 63438 "PTE Probe"
        {
            SingleInstance = true;
            EventSubscriberInstance = Manual;

            var
                ModifyCount: Integer;
                OpenCount: Integer;
                LastRec: Text[50];
                LastXRec: Text[50];
                Veto: Boolean;

            procedure Reset(NewVeto: Boolean)
            begin
                ModifyCount := 0;
                OpenCount := 0;
                LastRec := '';
                LastXRec := '';
                Veto := NewVeto;
            end;

            procedure GetModifyCount(): Integer begin exit(ModifyCount); end;
            procedure GetOpenCount(): Integer begin exit(OpenCount); end;
            procedure GetLastRec(): Text[50] begin exit(LastRec); end;
            procedure GetLastXRec(): Text[50] begin exit(LastXRec); end;

            [EventSubscriber(ObjectType::Page, Page::"PTE Rows", 'OnOpenPageEvent', '', false, false)]
            local procedure OnOpen(var Rec: Record "PTE Row")
            begin
                OpenCount += 1;
            end;

            [EventSubscriber(ObjectType::Page, Page::"PTE Rows", 'OnModifyRecordEvent', '', false, false)]
            local procedure OnModify(var Rec: Record "PTE Row"; var xRec: Record "PTE Row"; var AllowModify: Boolean)
            begin
                ModifyCount += 1;
                LastRec := Rec."Value";
                LastXRec := xRec."Value";
                if Veto then
                    AllowModify := false;
            end;
        }

        codeunit 63439 "PTE Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Seed()
            var
                Row: Record "PTE Row";
            begin
                Row.DeleteAll();
                Row.Init();
                Row."Code" := 'L1';
                Row."Value" := 'BEFORE';
                Row.Insert();
                Commit();
            end;

            // POSITIVE: the runner registers a page-scoped subscriber where BC's own
            // NavForm.RaiseOn...Async looks for it, so both a lifecycle event and a
            // record-write event reach it, carrying the values BC passes.
            [Test]
            procedure PageEvents_ReachAManuallyBoundSubscriber()
            var
                Probe: Codeunit "PTE Probe";
                Rows: TestPage "PTE Rows";
            begin
                Seed();
                Probe.Reset(false);
                BindSubscription(Probe);
                Rows.OpenEdit();
                Rows.GoToKey('L1');
                Rows.DirectSave.Invoke();
                Rows.Close();
                UnbindSubscription(Probe);

                if Probe.GetOpenCount() <> 1 then
                    Error('PTE1 FAIL: expected OnOpenPageEvent once, got %1', Probe.GetOpenCount());
                if Probe.GetModifyCount() <> 1 then
                    Error('PTE2 FAIL: expected OnModifyRecordEvent once, got %1', Probe.GetModifyCount());
                if Probe.GetLastRec() <> 'DIRECT' then
                    Error('PTE3 FAIL: expected Rec."Value"=DIRECT in the event, got %1', Probe.GetLastRec());
                if Probe.GetLastXRec() <> 'BEFORE' then
                    Error('PTE4 FAIL: expected xRec."Value"=BEFORE in the event, got %1', Probe.GetLastXRec());
            end;

            // NEGATIVE: the subscriber's answer is READ BACK. A registration that dispatched
            // and discarded AllowModify would pass the positive test above and fail here.
            [Test]
            procedure AllowModifyFalse_LeavesTheRowUnwritten()
            var
                Probe: Codeunit "PTE Probe";
                Row: Record "PTE Row";
                Rows: TestPage "PTE Rows";
            begin
                Seed();
                Probe.Reset(true);
                BindSubscription(Probe);
                Rows.OpenEdit();
                Rows.GoToKey('L1');
                Rows.DirectSave.Invoke();
                Rows.Close();
                UnbindSubscription(Probe);

                if Probe.GetModifyCount() <> 1 then
                    Error('PTE5 FAIL: expected the veto subscriber to run once, got %1', Probe.GetModifyCount());
                Row.Get('L1');
                if Row."Value" <> 'BEFORE' then
                    Error('PTE6 FAIL: expected the row unchanged after AllowModify := false, got %1', Row."Value");
            end;

            // NEGATIVE, the other direction: an UNBOUND Manual subscriber must see nothing,
            // so the registration cannot be a blanket "always call every discovered method".
            [Test]
            procedure UnboundManualSubscriber_SeesNothing()
            var
                Probe: Codeunit "PTE Probe";
                Rows: TestPage "PTE Rows";
            begin
                Seed();
                Probe.Reset(false);
                Rows.OpenEdit();
                Rows.GoToKey('L1');
                Rows.DirectSave.Invoke();
                Rows.Close();

                if Probe.GetModifyCount() <> 0 then
                    Error('PTE7 FAIL: expected an unbound Manual subscriber to see nothing, got %1', Probe.GetModifyCount());
                if Probe.GetOpenCount() <> 0 then
                    Error('PTE8 FAIL: expected an unbound Manual subscriber to see no open event, got %1', Probe.GetOpenCount());
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all three page-trigger-event tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit63439.PageEvents_ReachAManuallyBoundSubscriber", output);
        Assert.Contains("PASS  Codeunit63439.AllowModifyFalse_LeavesTheRowUnwritten", output);
        Assert.Contains("PASS  Codeunit63439.UnboundManualSubscriber_SeesNothing", output);
    }
}
