using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2469: a subpage part whose SubPageLink carries a
/// <c>const(...)</c> or <c>filter(...)</c> entry was refused out-of-scope by
/// <c>MockTestPage.SubPageLinks</c> ("only FilterType.FIELD SubPageLinks are implemented"),
/// even though such a link is ordinary AL — 10.9% of Base Application 28.1's SubPageLink
/// entries (measured in the issue), and the shape behind 19 Tests-ERM failures.
///
/// <c>LiveNavTestPart.ApplyLink</c> now applies a CONST link as a single-value filter and a
/// FILTER link as the filter expression on the part's own field, next to the FIELD links it
/// already applied; a const/filter-only link no longer demands a parent record either.
///
/// The BEHAVIORAL claim (what BC shows through such a part, and what a New() through it
/// stamps) is a plain-BC-behaviour claim and belongs upstream — see
/// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60324 "TSPL Tests"
/// (TestPageSubpagePartConstFilter.al), per .claude/rules/bc-behavior-tests-go-upstream.md.
/// This test exists so a regression in OUR OWN link-application mechanism fails loudly here,
/// spawning the real runner against a synthetic bundle, without depending on the submodule
/// pin having moved yet.
///
/// No Library Assert dependency (no "application" in the fixture's app.json — see
/// .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own Error() with the
/// observed value, so the runner's own PASS/FAIL output is the assertion surface.
/// </summary>
public class TestPagePartConstFilterLinkTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-testpage-part-const-filter-link-2469", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c2469000-0000-4000-8000-000000002469",
          "name": "TestPagePartConstFilterLink2469",
          "publisher": "Repro2469",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62540, "to": 62549 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "TpcfHeader.Table.al"), """
        table 62540 "Tpcf Header"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Code[20]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TpcfLine.Table.al"), """
        table 62541 "Tpcf Line"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Header No."; Code[20]) { }
                field(2; "Line No."; Integer) { }
                field(3; Kind; Option) { OptionMembers = Comment,Attachment; }
                field(4; Status; Option) { OptionMembers = "None",Open,Released,Closed; }
                field(5; Name; Text[50]) { }
                field(6; "Table ID"; Integer) { }
                field(7; Category; Code[10]) { }
            }
            keys { key(PK; "Header No.", "Line No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TpcfLines.Page.al"), """
        page 62542 "Tpcf Lines"
        {
            PageType = ListPart;
            SourceTable = "Tpcf Line";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field("Line No."; Rec."Line No.") { ApplicationArea = All; }
                        field(Kind; Rec.Kind) { ApplicationArea = All; }
                        field(Status; Rec.Status) { ApplicationArea = All; }
                        field(Name; Rec.Name) { ApplicationArea = All; }
                        field("Table ID"; Rec."Table ID") { ApplicationArea = All; }
                        field(Category; Rec.Category) { ApplicationArea = All; }
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TpcfCard.Page.al"), """
        page 62543 "Tpcf Card"
        {
            PageType = Card;
            SourceTable = "Tpcf Header";
            ApplicationArea = All;
            UsageCategory = None;

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                    part(ConstLines; "Tpcf Lines")
                    {
                        ApplicationArea = All;
                        SubPageLink = "Header No." = field("No."), Kind = const(Attachment);
                    }
                    part(FilterLines; "Tpcf Lines")
                    {
                        ApplicationArea = All;
                        SubPageLink = "Header No." = field("No."), Status = filter(Open | Released);
                    }
                    part(ConstTableLines; "Tpcf Lines")
                    {
                        ApplicationArea = All;
                        SubPageLink = "Header No." = field("No."), "Table ID" = const(Database::"Tpcf Header");
                    }
                    part(ConstCodeLines; "Tpcf Lines")
                    {
                        ApplicationArea = All;
                        SubPageLink = "Header No." = field("No."), Category = const('SPECIAL');
                    }
                    part(ConstOnlyLines; "Tpcf Lines")
                    {
                        ApplicationArea = All;
                        SubPageLink = Kind = const(Comment);
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TpcfTests.Codeunit.al"), """
        codeunit 62544 "Tpcf Tests"
        {
            Subtype = Test;

            local procedure AddLine(HeaderNo: Code[20]; LineNo: Integer; Kind: Option Comment,Attachment; Status: Option "None",Open,Released,Closed; Name: Text[50]; TableId: Integer; Category: Code[10])
            var
                Line: Record "Tpcf Line";
            begin
                Line.Init();
                Line."Header No." := HeaderNo;
                Line."Line No." := LineNo;
                Line.Kind := Kind;
                Line.Status := Status;
                Line.Name := Name;
                Line."Table ID" := TableId;
                Line.Category := Category;
                Line.Insert();
            end;

            local procedure Initialize()
            var
                Header: Record "Tpcf Header";
                Line: Record "Tpcf Line";
            begin
                Header.DeleteAll();
                Line.DeleteAll();
                Header.Init();
                Header."No." := 'H1';
                Header.Insert();
                Header.Init();
                Header."No." := 'H2';
                Header.Insert();
                AddLine('H1', 1, Line.Kind::Comment, Line.Status::Open, 'C-Open', 0, '');
                AddLine('H1', 2, Line.Kind::Attachment, Line.Status::Open, 'A-Open', Database::"Tpcf Header", 'SPECIAL');
                AddLine('H1', 3, Line.Kind::Attachment, Line.Status::Closed, 'A-Closed', 0, 'SPECIAL');
                AddLine('H1', 4, Line.Kind::Comment, Line.Status::Released, 'C-Rel', Database::"Tpcf Header", '');
                AddLine('H2', 1, Line.Kind::Attachment, Line.Status::Open, 'Foreign', Database::"Tpcf Header", 'SPECIAL');
            end;

            local procedure OpenCardOn(HeaderNo: Code[20]; var Card: TestPage "Tpcf Card")
            var
                Header: Record "Tpcf Header";
            begin
                Header.Get(HeaderNo);
                Card.OpenEdit();
                Card.GoToRecord(Header);
            end;

            local procedure ConstLinesNames(var Card: TestPage "Tpcf Card") Names: Text
            begin
                if not Card.ConstLines.First() then
                    exit('');
                repeat
                    if Card.ConstLines."Line No.".Value <> '0' then
                        Names += Card.ConstLines.Name.Value + ';';
                until not Card.ConstLines.Next();
            end;

            local procedure FilterLinesNames(var Card: TestPage "Tpcf Card") Names: Text
            begin
                if not Card.FilterLines.First() then
                    exit('');
                repeat
                    if Card.FilterLines."Line No.".Value <> '0' then
                        Names += Card.FilterLines.Name.Value + ';';
                until not Card.FilterLines.Next();
            end;

            local procedure ConstTableLinesNames(var Card: TestPage "Tpcf Card") Names: Text
            begin
                if not Card.ConstTableLines.First() then
                    exit('');
                repeat
                    if Card.ConstTableLines."Line No.".Value <> '0' then
                        Names += Card.ConstTableLines.Name.Value + ';';
                until not Card.ConstTableLines.Next();
            end;

            local procedure ConstCodeLinesNames(var Card: TestPage "Tpcf Card") Names: Text
            begin
                if not Card.ConstCodeLines.First() then
                    exit('');
                repeat
                    if Card.ConstCodeLines."Line No.".Value <> '0' then
                        Names += Card.ConstCodeLines.Name.Value + ';';
                until not Card.ConstCodeLines.Next();
            end;

            local procedure ConstOnlyLinesNames(var Card: TestPage "Tpcf Card") Names: Text
            begin
                if not Card.ConstOnlyLines.First() then
                    exit('');
                repeat
                    if Card.ConstOnlyLines."Line No.".Value <> '0' then
                        Names += Card.ConstOnlyLines.Name.Value + ';';
                until not Card.ConstOnlyLines.Next();
            end;

            [Test]
            procedure ConstLink_ShowsOnlyMatchingRows()
            var
                Card: TestPage "Tpcf Card";
                Names: Text;
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Names := ConstLinesNames(Card);
                Card.Close();
                if Names <> 'A-Open;A-Closed;' then
                    Error('const(Attachment) + field link: expected A-Open;A-Closed; got %1', Names);
            end;

            [Test]
            procedure FilterLink_ShowsOnlyRowsInsideExpression()
            var
                Card: TestPage "Tpcf Card";
                Names: Text;
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Names := FilterLinesNames(Card);
                Card.Close();
                if Names <> 'C-Open;A-Open;C-Rel;' then
                    Error('filter(Open | Released) + field link: expected C-Open;A-Open;C-Rel; got %1', Names);
            end;

            [Test]
            procedure ConstDatabaseLink_PinsTableId()
            var
                Card: TestPage "Tpcf Card";
                Names: Text;
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Names := ConstTableLinesNames(Card);
                Card.Close();
                if Names <> 'A-Open;C-Rel;' then
                    Error('const(Database::"Tpcf Header") + field link: expected A-Open;C-Rel; got %1', Names);
            end;

            [Test]
            procedure ConstTextLink_PinsCodeField()
            var
                Card: TestPage "Tpcf Card";
                Names: Text;
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Names := ConstCodeLinesNames(Card);
                Card.Close();
                if Names <> 'A-Open;A-Closed;' then
                    Error('const(''SPECIAL'') + field link on a Code field: expected A-Open;A-Closed; got %1', Names);
            end;

            [Test]
            procedure ConstOnlyLink_FiltersWithoutFieldLink()
            var
                Card: TestPage "Tpcf Card";
                Names: Text;
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Names := ConstOnlyLinesNames(Card);
                Card.Close();
                if Names <> 'C-Open;C-Rel;' then
                    Error('const(Comment)-only link: expected C-Open;C-Rel; got %1', Names);
            end;

            [Test]
            procedure ConstLink_NewStampsConstant()
            var
                Line: Record "Tpcf Line";
                Card: TestPage "Tpcf Card";
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Card.ConstLines.New();
                Card.ConstLines."Line No.".SetValue(9);
                Card.ConstLines.Name.SetValue('New-A');
                Card.Close();
                if not Line.Get('H1', 9) then
                    Error('New() through the const-linked part must have inserted line 9 under H1');
                if Line.Kind <> Line.Kind::Attachment then
                    Error('const(Attachment) link must have stamped Kind onto the new row, got %1', Line.Kind);
            end;

            [Test]
            procedure FilterLink_NewDoesNotStampMultiValueExpression()
            var
                Line: Record "Tpcf Line";
                Card: TestPage "Tpcf Card";
            begin
                Initialize();
                OpenCardOn('H1', Card);
                Card.FilterLines.New();
                Card.FilterLines."Line No.".SetValue(9);
                Card.FilterLines.Name.SetValue('New-F');
                Card.Close();
                if not Line.Get('H1', 9) then
                    Error('New() through the filter-linked part must have inserted line 9 under H1');
                if Line.Status <> Line.Status::"None" then
                    Error('filter(Open | Released) has no single value to stamp; Status must stay None, got %1', Line.Status);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void ConstAndFilterLinks_FilterThePart()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(WriteBundle());

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62544.ConstLink_ShowsOnlyMatchingRows", output);
        Assert.Contains("PASS  Codeunit62544.FilterLink_ShowsOnlyRowsInsideExpression", output);
        Assert.Contains("PASS  Codeunit62544.ConstDatabaseLink_PinsTableId", output);
        Assert.Contains("PASS  Codeunit62544.ConstOnlyLink_FiltersWithoutFieldLink", output);
        Assert.Contains("PASS  Codeunit62544.ConstTextLink_PinsCodeField", output);
        Assert.DoesNotContain("FAIL", output);
        Assert.DoesNotContain("testpage-part-link", output);
    }

    [SkippableFact]
    public void ConstAndFilterLinks_NewStampsOnlySingleValues()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(WriteBundle());

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62544.ConstLink_NewStampsConstant", output);
        Assert.Contains("PASS  Codeunit62544.FilterLink_NewDoesNotStampMultiValueExpression", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
