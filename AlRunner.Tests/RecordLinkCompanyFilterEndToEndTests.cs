// RecordLinkCompanyFilterEndToEndTests — issues #4956 and #4961.
//
// RUNNER-MECHANISM test: the Record Link store (RecordPatches.RecordLinkTable.cs) selects a
// record's links the way BC's RecordLink.SetupRecordLinkRanges does, by Record ID and, for a
// per-company table, by Company = the record's company; DeleteLink(ID) compares Company
// whatever the table; and a Rename reaches RecordLinkStore_Move (#4961), which moves only the
// rows that filter selects. What BC does is measured upstream by corpus codeunit 67681
// "Test Record Link Columns"; this pins that the runner's own store answers the same.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordLinkCompanyFilterEndToEndTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public RecordLinkCompanyFilterEndToEndTests()
    {
        _root = TestScratch.Dir("al-runner-record-link-company-e2e");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" \"--package-cache\" \"{platformApps}\"");
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

    private void WriteFixture()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "0b4956a1-4956-4956-4956-495649564956",
          "name": "Record Link Company Filter E2E Repro",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 94961, "to": 94969 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Fixture.al"), """
        table 94961 "RLF Host"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        table 94962 "RLF Shared Host"
        {
            DataPerCompany = false;
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit 94963 "RLF Tests"
        {
            Subtype = Test;

            local procedure InsertRow(RecId: RecordId; Url: Text[2048]; LinkCompany: Text[30]): Integer
            var
                Link: Record "Record Link";
            begin
                Link.Init();
                Link."Record ID" := RecId;
                Link.URL1 := Url;
                Link.Company := LinkCompany;
                Link.Insert(true);
                exit(Link."Link ID");
            end;

            local procedure RowCount(RecId: RecordId): Integer
            var
                Link: Record "Record Link";
            begin
                Link.SetRange("Record ID", RecId);
                exit(Link.Count());
            end;

            [Test]
            procedure HasLinks_PerCompany_FiltersCompany()
            var
                Host: Record "RLF Host";
            begin
                Host.Code := 'H1';
                Host.Insert();
                InsertRow(Host.RecordId(), 'https://other.example', 'OTHER CO');
                InsertRow(Host.RecordId(), 'https://blank.example', '');
                if Host.HasLinks() then
                    Error('HASLINKS-OTHER a row of another or no company counted as a link');
                InsertRow(Host.RecordId(), 'https://own.example', CompanyName());
                if not Host.HasLinks() then
                    Error('HASLINKS-OWN the current company''s row was not found');
            end;

            [Test]
            procedure HasLinks_NotPerCompany_NoFilter()
            var
                Shared: Record "RLF Shared Host";
            begin
                Shared.Code := 'S1';
                Shared.Insert();
                InsertRow(Shared.RecordId(), 'https://other.example', 'OTHER CO');
                if not Shared.HasLinks() then
                    Error('HASLINKS-SHARED a table not per company must not filter on Company');
            end;

            [Test]
            procedure DeleteLinks_PerCompany_KeepsOtherCompany()
            var
                Host: Record "RLF Host";
                Link: Record "Record Link";
                OtherId: Integer;
            begin
                Host.Code := 'H2';
                Host.Insert();
                OtherId := InsertRow(Host.RecordId(), 'https://other.example', 'OTHER CO');
                Host.AddLink('https://own.example', 'own');
                Host.DeleteLinks();
                if RowCount(Host.RecordId()) <> 1 then
                    Error('DELETELINKS-COUNT expected 1 row left, got %1', RowCount(Host.RecordId()));
                if not Link.Get(OtherId) then
                    Error('DELETELINKS-OTHER the other company''s row was deleted');
            end;

            [Test]
            procedure DeleteLink_ComparesCompanyWhateverTheTable()
            var
                Host: Record "RLF Host";
                Shared: Record "RLF Shared Host";
                Link: Record "Record Link";
                OtherId: Integer;
                SharedId: Integer;
                OwnId: Integer;
            begin
                Host.Code := 'H3';
                Host.Insert();
                Shared.Code := 'S3';
                Shared.Insert();
                OtherId := InsertRow(Host.RecordId(), 'https://other.example', 'OTHER CO');
                OwnId := Host.AddLink('https://own.example', 'own');
                SharedId := Shared.AddLink('https://shared.example', 'shared');
                Host.DeleteLink(OtherId);
                Host.DeleteLink(OwnId);
                Shared.DeleteLink(SharedId);
                if not Link.Get(OtherId) then
                    Error('DELETELINK-OTHER the other company''s row was deleted');
                if Link.Get(OwnId) then
                    Error('DELETELINK-OWN the current company''s row was not deleted');
                if not Link.Get(SharedId) then
                    Error('DELETELINK-SHARED the empty-Company row of a table not per company was deleted');
            end;

            [Test]
            procedure CopyLinks_PerCompanySource_CopiesOnlyOwnRows()
            var
                Source: Record "RLF Host";
                Target: Record "RLF Host";
            begin
                Source.Code := 'SRC';
                Source.Insert();
                Target.Code := 'DST';
                Target.Insert();
                InsertRow(Source.RecordId(), 'https://other.example', 'OTHER CO');
                Source.AddLink('https://own.example', 'own');
                Target.CopyLinks(Source);
                if RowCount(Target.RecordId()) <> 1 then
                    Error('COPYLINKS-COUNT expected 1 copied row, got %1', RowCount(Target.RecordId()));
            end;

            [Test]
            procedure Rename_MovesOnlyOwnRows()
            var
                Host: Record "RLF Host";
                Renamed: Record "RLF Host";
                OldRecId: RecordId;
            begin
                Host.Code := 'OLD';
                Host.Insert();
                OldRecId := Host.RecordId();
                InsertRow(OldRecId, 'https://other.example', 'OTHER CO');
                Host.AddLink('https://own.example', 'own');
                Host.Rename('NEW');
                Renamed.Get('NEW');
                if RowCount(Renamed.RecordId()) <> 1 then
                    Error('RENAME-MOVED expected 1 row on the new key, got %1', RowCount(Renamed.RecordId()));
                if RowCount(OldRecId) <> 1 then
                    Error('RENAME-LEFT expected the other company''s row on the old key, got %1', RowCount(OldRecId));
            end;
        }
        """);
    }

    [SkippableFact]
    public void RecordLinkReaders_FilterCompanyLikeBc_AndRenameMovesTheLinks()
    {
        WriteFixture();
        var (output, exit) = RunRunner(_root);
        TestArtifacts.SkipIf(output.Contains("no BC artifact") || output.Contains("[bc] no engines"),
            "no BC engine artifact provisioned in this environment");

        Assert.True(exit == 0, $"expected all six AL tests to pass; exit={exit}\n{output}");
        Assert.Contains("   passed 6 ", output);
    }
}
