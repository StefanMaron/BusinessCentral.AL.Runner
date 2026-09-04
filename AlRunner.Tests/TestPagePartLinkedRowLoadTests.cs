using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2677: a subpage part bound via SubPageLink never got its
/// own row positioned or its own OnAfterGetRecord/OnAfterGetCurrRecord run at all —
/// TestPageFactory.TryBuild hands GetPart a BLANK, unfetched record, and nothing ever made an
/// explicit MoveXxx/GoToBookmark call on the part's behalf.
///
/// AlRunner/Patches/MockTestPage.cs's LiveNavTestPage.EagerlyBuildParts (called from
/// RunnerTestPageState.MarkOpened right after the host's own OnOpenPage, before the host's
/// first row is found) reaches every declared part control eagerly, and
/// LiveNavTestPage.Loaded refreshes every linked part in _parts (via the new, deliberately
/// UN-guarded LiveNavTestPart.ReloadLinkedRow) on every subsequent host row load — which is
/// what makes a GoToRecord on the host refresh the part too.
///
/// The BEHAVIORAL claim ("a subpage part linked via SubPageLink fires OnOpenPage/
/// OnAfterGetRecord/OnAfterGetCurrRecord eagerly when the host opens, with nothing ever
/// touching CurrPage.&lt;part&gt;/TestPage.&lt;part&gt;, and re-fires for a new row on
/// GoToRecord") is a plain-BC-behaviour claim and belongs upstream — see
/// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60815 "Test Page Part Agcr Tests"
/// (corpus PR #141, all 8 BC legs green, independently confirmed against a local BC 28.4
/// container), per .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a
/// regression in OUR OWN eager-build/refresh-on-load mechanism fails loudly here, spawning
/// the real runner against a synthetic bundle, without depending on the submodule pin having
/// moved yet.
///
/// No Library Assert dependency (no "application" in the fixture's app.json — see
/// .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own Error() with the
/// observed value, so the runner's own PASS/FAIL output is the assertion surface.
/// </summary>
public class TestPagePartLinkedRowLoadTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
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

    private static string WriteBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-testpage-part-linked-row-load-2677", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c2677000-0000-4000-8000-000000002677",
          "name": "TestPagePartLinkedRowLoad2677",
          "publisher": "Repro2677",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62525, "to": 62535 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "TplrRow.Table.al"), """
        table 62525 "Tplr Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Name; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);

        // Fire counter lives in its OWN table, one row per key, incremented via Insert-then-
        // Delete-if-exists (never Modify — Modify on the SAME row does not re-run
        // OnAfterGetRecord, so accumulating there could not double-count even if the part
        // fired twice; a fresh row per fire, keyed by an ever-growing counter, sidesteps that
        // question entirely and just counts Insert calls).
        File.WriteAllText(Path.Combine(root, "TplrFire.Table.al"), """
        table 62529 "Tplr Fire"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; "No."; Code[20]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TplrPart.Page.al"), """
        page 62526 "Tplr Part"
        {
            PageType = CardPart;
            SourceTable = "Tplr Row";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                }
            }

            trigger OnAfterGetCurrRecord()
            var
                Fire: Record "Tplr Fire";
            begin
                Fire.Init();
                Fire."No." := Rec."No.";
                Fire.Insert(true);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(root, "TplrHost.Page.al"), """
        page 62527 "Tplr Host"
        {
            PageType = Card;
            SourceTable = "Tplr Row";
            ApplicationArea = All;
            UsageCategory = None;

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                }
                area(FactBoxes)
                {
                    part(TplrPart; "Tplr Part")
                    {
                        ApplicationArea = All;
                        SubPageLink = "No." = field("No.");
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TplrTests.Codeunit.al"), """
        codeunit 62528 "Tplr Tests"
        {
            Subtype = Test;

            local procedure Initialize()
            var
                Row: Record "Tplr Row";
                Fire: Record "Tplr Fire";
            begin
                Row.DeleteAll();
                Fire.DeleteAll();
            end;

            local procedure SeedRow(No: Code[20]; Name: Text[50])
            var
                Row: Record "Tplr Row";
            begin
                Row.Init();
                Row."No." := No;
                Row.Name := Name;
                Row.Insert();
            end;

            local procedure FireCountFor(No: Code[20]): Integer
            var
                Fire: Record "Tplr Fire";
            begin
                Fire.SetRange("No.", No);
                exit(Fire.Count());
            end;

            // Positive: the part's OnAfterGetCurrRecord must have fired for the FIRST row,
            // WITHOUT the test ever touching CurrPage.TplrPart/Host.TplrPart.
            [Test]
            procedure NoTouch_PartFiresOnOpenView()
            var
                Host: TestPage "Tplr Host";
                FireCount: Integer;
            begin
                Initialize();
                SeedRow('X', 'Alpha');

                Host.OpenView();
                Host.Close();

                FireCount := FireCountFor('X');
                if FireCount = 0 then
                    Error('expected the part''s OnAfterGetCurrRecord to have fired at least once with nothing touching the part, got %1', FireCount);
            end;

            // Positive: GoToRecord to a DIFFERENT row must re-fire the part's own trigger for
            // the new row, and must NOT fire again for the row just left.
            [Test]
            procedure GoToRecord_PartRefires()
            var
                Row2: Record "Tplr Row";
                Host: TestPage "Tplr Host";
                FireCountXAfterOpen: Integer;
                FireCountYAfterGoTo: Integer;
                FireCountXAfterGoTo: Integer;
                PartNo: Code[20];
            begin
                Initialize();
                SeedRow('X', 'Alpha');
                SeedRow('Y', 'Bravo');
                Row2.Get('Y');

                Host.OpenView();
                FireCountXAfterOpen := FireCountFor('X');
                if not Host.GoToRecord(Row2) then
                    Error('GoToRecord must find the seeded row Y');
                PartNo := Host.TplrPart."No.".Value();
                Host.Close();

                FireCountYAfterGoTo := FireCountFor('Y');
                FireCountXAfterGoTo := FireCountFor('X');

                if FireCountXAfterOpen = 0 then
                    Error('expected at least one fire for X after OpenView, got %1', FireCountXAfterOpen);
                if FireCountYAfterGoTo = 0 then
                    Error('expected the part to have fired for the NEW row Y after GoToRecord, got %1', FireCountYAfterGoTo);
                if FireCountXAfterGoTo <> FireCountXAfterOpen then
                    Error('expected the part to NOT re-fire for the row just left (X); before=%1 after=%2', FireCountXAfterOpen, FireCountXAfterGoTo);
                if PartNo <> 'Y' then
                    Error('expected the part''s control to reflect the NEW row Y, got %1', PartNo);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void NoTouch_PartFiresOnOpenView()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(WriteBundle());

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62528.NoTouch_PartFiresOnOpenView", output);
        Assert.DoesNotContain("FAIL", output);
    }

    [SkippableFact]
    public void GoToRecord_PartRefires()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(WriteBundle());

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62528.GoToRecord_PartRefires", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
