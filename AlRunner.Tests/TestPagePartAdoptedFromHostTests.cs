// TestPagePartAdoptedFromHostTests — issue #2201 (TestPage.<part> and the host's own
// CurrPage.<part>.Page used to be two DIFFERENT NavForm instances for the same subpage
// control).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR
// OWN dispatch path reaches the SAME NavForm the host's own compiled AL gets back from
// CurrPage.<part>, rather than building a second, disconnected instance the way
// TestPageFactory.TryBuild/TryBuildRecordless alone used to. Two mechanisms, one per test:
//
//   - HostSeededTemporaryPart_TestPageSeesTheSameRow: RunnerPageInstance.AdoptFromHost
//     reaches the host's own live subpage object via NavForm.GetPart(int) and reifies it in
//     place, reusing an already-bound record instead of rebinding a fresh empty one.
//   - HostWriteBeforeTestPageTouch_TestPageSeesTheHostWrite: RunnerFormInit
//     .OnSubpagePartResolved, a Cecil-injected call appended to NavForm.GetPart(int) itself,
//     reifies a page-globals part (running its OnOpenPage) the FIRST time EITHER side reaches
//     it through GetPart — needed because a page-globals part's host write is a plain
//     procedure call that never touches EnsureMetadataLoaded, so AdoptFromHost's own
//     "already touched" check (which the first mechanism relies on) has nothing to observe.
//
// The BEHAVIORAL claims ("a SourceTableTemporary part's rows, pushed from the host's own
// OnOpenPage, must be visible to both sides"; "a page-globals part's host write must be
// visible to the TestPage side, even when the host writes before the TestPage side ever
// touches the part") are proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests, "TP SrcTemp Tests" (codeunit 60807) and
// "Test Page NoSrc Part Tests" (codeunit 60803), per
// .claude/rules/bc-behavior-tests-go-upstream.md. These tests exist so a regression in OUR
// OWN instance-adoption mechanisms fails loudly here, without needing the corpus's
// al-language submodule pin bumped first.
//
// RED/GREEN proof (both confirmed by temporarily disabling the fix and rebuilding):
//   - Reverting RunnerPageInstance.AdoptFromHost to always return null makes
//     HostSeededTemporaryPart_TestPageSeesTheSameRow fail: TestPage.Lines.First() finds no
//     row, because the disconnected instance TryBuild constructs never received the row the
//     host's OnOpenPage pushed into the ADOPTED instance's own temporary Rec.
//   - Emptying RunnerFormInit.OnSubpagePartResolved's body makes
//     HostWriteBeforeTestPageTouch_TestPageSeesTheHostWrite fail: TestPage.Info.Tag.Value()
//     reads 'Hello' (the part's own OnOpenPage, run LATE once the TestPage side finally asks)
//     instead of 'FROM-HOST' (what the host already wrote).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPagePartAdoptedFromHostTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPagePartAdoptedFromHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-testpage-part-adopted-from-host", Guid.NewGuid().ToString("N"));
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
    /// A ListPart declaring SourceTableTemporary, hosted on a Card page that pushes one row
    /// into it from OnOpenPage — before the TestPage side ever touches the part. The part's
    /// own temporary Rec only exists on whichever instance the host wrote through; a
    /// disconnected second instance has an EMPTY table, not merely a stale value.
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "d2e3f4a5-6b7c-4890-9d0e-1f2a3b4c5d02",
          "name": "Runner Mechanism - TestPage Part Adopted From Host",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62410, "to": 62413 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahRow.Table.al"), """
        table 62410 "Tpah Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Name; Text[50]) { }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahPart.Page.al"), """
        page 62411 "Tpah Part"
        {
            PageType = ListPart;
            SourceTable = "Tpah Row";
            SourceTableTemporary = true;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field(Name; Rec.Name)
                        {
                            ApplicationArea = All;
                        }
                    }
                }
            }

            internal procedure SetRows(var TempRow: Record "Tpah Row" temporary)
            begin
                Rec.DeleteAll();
                if TempRow.FindSet() then
                    repeat
                        Rec := TempRow;
                        Rec.Insert();
                    until TempRow.Next() = 0;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahHost.Page.al"), """
        page 62412 "Tpah Host"
        {
            PageType = Card;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    part(Lines; "Tpah Part")
                    {
                        ApplicationArea = All;
                    }
                }
            }

            trigger OnOpenPage()
            var
                TempRow: Record "Tpah Row" temporary;
            begin
                TempRow."Entry No." := 1;
                TempRow.Name := 'FROM-HOST';
                TempRow.Insert();
                CurrPage.Lines.Page.SetRows(TempRow);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahTests.Codeunit.al"), """
        codeunit 62413 "Tpah Tests"
        {
            Subtype = Test;

            [Test]
            procedure HostSeededTemporaryPart_TestPageSeesTheSameRow()
            var
                Host: TestPage "Tpah Host";
            begin
                Host.OpenEdit();

                if not Host.Lines.First() then
                    Error('TestPage.Lines must show the row the host inserted from OnOpenPage — the part page instance must be the SAME one the host wrote through');
                if Host.Lines.Name.Value() <> 'FROM-HOST' then
                    Error('the row value must be what the host wrote, got: ' + Host.Lines.Name.Value());

                Host.Close();
            end;
        }
        """);
    }

    /// <summary>
    /// A CardPart whose OWN page declares no SourceTable at all — every control is bound to
    /// a page global, and the page's own AL is the only thing that ever writes one. The host
    /// writes through it (a plain procedure call, <c>CurrPage.Info.Page.SetTag(...)</c>) from
    /// its OWN field's OnValidate, driven from the TESTPAGE side BEFORE the TestPage side
    /// ever reads <c>Host.Info</c> — the exact ordering issue #2201's own repro used, and the
    /// one AdoptFromHost alone could not fix (a plain procedure call never touches
    /// EnsureMetadataLoaded, so there is no signal AdoptFromHost's own "already touched"
    /// check can observe). RunnerFormInit.OnSubpagePartResolved (the Cecil hook appended to
    /// NavForm.GetPart(int)) is what fixes this: it reifies a page-globals part — including
    /// running its own OnOpenPage — the FIRST time EITHER side reaches it through GetPart,
    /// before returning the object to whichever side asked.
    /// </summary>
    private void WriteGlobalsRaceBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "e3f4a5b6-7c8d-4901-ae1f-2a3b4c5d6e03",
          "name": "Runner Mechanism - TestPage Part Globals Race",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62420, "to": 62429 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpgrInfo.Page.al"), """
        page 62420 "Tpgr Info"
        {
            PageType = CardPart;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    field(Tag; TagValue)
                    {
                        ApplicationArea = All;
                    }
                }
            }

            trigger OnOpenPage()
            begin
                TagValue := 'Hello';
            end;

            var
                TagValue: Text;

            internal procedure SetTag(NewTag: Text)
            begin
                TagValue := NewTag;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpgrHost.Page.al"), """
        page 62421 "Tpgr Host"
        {
            PageType = Card;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    field(Mode; SelectedMode)
                    {
                        ApplicationArea = All;

                        trigger OnValidate()
                        begin
                            CurrPage.Info.Page.SetTag(SelectedMode);
                        end;
                    }

                    part(Info; "Tpgr Info")
                    {
                        ApplicationArea = All;
                    }
                }
            }

            var
                SelectedMode: Text;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpgrTests.Codeunit.al"), """
        codeunit 62422 "Tpgr Tests"
        {
            Subtype = Test;

            [Test]
            procedure HostWriteBeforeTestPageTouch_TestPageSeesTheHostWrite()
            var
                Host: TestPage "Tpgr Host";
            begin
                Host.OpenEdit();
                Host.Mode.SetValue('FROM-HOST');

                if Host.Info.Tag.Value() <> 'FROM-HOST' then
                    Error('TestPage.Info must be the SAME instance the host already wrote through; got: ' + Host.Info.Tag.Value());

                Host.Close();
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
    /// Positive: the TestPage's own read must see the row through the SAME part page instance
    /// the host's OnOpenPage wrote through, not a disconnected second one seeded from nothing.
    /// </summary>
    [SkippableFact]
    public void HostSeededTemporaryPart_TestPageSeesTheSameRow()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62413.HostSeededTemporaryPart_TestPageSeesTheSameRow", output);
        Assert.DoesNotContain("FAIL", output);
    }

    /// <summary>
    /// Positive: a page-globals part the host already wrote through (via a plain procedure
    /// call, never touching EnsureMetadataLoaded) must still read the host's write from the
    /// TestPage side — proving the Cecil hook on NavForm.GetPart(int) reifies the part (and
    /// runs its OnOpenPage) before either side's first real touch, not just before the
    /// runner's own AdoptFromHost call.
    ///
    /// RED/GREEN proof: with RunnerFormInit.OnSubpagePartResolved's body emptied (the
    /// pre-fix shape — GetPart(int) still gets hooked, but the hook does nothing), this
    /// fails: TestPage.Info.Tag.Value() reads 'Hello' (the part's own OnOpenPage, run LATE by
    /// AdoptFromHost when the TestPage side finally asks) instead of 'FROM-HOST' (what the
    /// host already wrote).
    /// </summary>
    [SkippableFact]
    public void HostWriteBeforeTestPageTouch_TestPageSeesTheHostWrite()
    {
        TestArtifacts.SkipIfMissing();

        WriteGlobalsRaceBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62422.HostWriteBeforeTestPageTouch_TestPageSeesTheHostWrite", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
