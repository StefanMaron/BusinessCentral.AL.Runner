// RecordLinkColumnsEndToEndTests — issue #4951.
//
// RUNNER-MECHANISM test: the Record Link store (RecordPatches.RecordLinkTable.cs) fills the
// columns BC's platform writes itself. What BC writes is measured upstream by corpus codeunit
// 67681 "Test Record Link Columns"; this pins where the runner reads each value from:
//   - "User ID" from ALDatabase.ALUserID — the skeleton user AL's UserId() answers, TESTUSER;
//   - Company from the skeleton company, only for a per-company parent table;
//   - SystemId from BC's own NCLMetaTable.SystemIdField, a fresh Guid per row;
//   - a CopyLinks copy cloned from the source row, so it keeps the source's author.
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordLinkColumnsEndToEndTests : IDisposable
{
    private readonly string _root;

    public RecordLinkColumnsEndToEndTests()
    {
        _root = TestScratch.Dir("al-runner-record-link-columns-e2e");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteFixture()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "0b4951a1-4951-4951-4951-495149514951",
          "name": "Record Link Columns E2E Repro",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 94951, "to": 94959 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Fixture.al"), """
        table 94951 "RLC Host"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        table 94952 "RLC Shared Host"
        {
            DataPerCompany = false;
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit 94953 "RLC Tests"
        {
            Subtype = Test;

            local procedure OnlyLinkOf(RecId: RecordId; var Link: Record "Record Link")
            begin
                Link.Reset();
                Link.SetRange("Record ID", RecId);
                if Link.Count() <> 1 then
                    Error('expected one Record Link row, got %1', Link.Count());
                Link.FindFirst();
            end;

            [Test]
            procedure AddLink_UserIdIsTheSkeletonUser()
            var
                Host: Record "RLC Host";
                Link: Record "Record Link";
            begin
                Host.Code := 'U';
                Host.Insert();
                Host.AddLink('https://u.example', 'user');
                OnlyLinkOf(Host.RecordId(), Link);
                if Link."User ID" <> 'TESTUSER' then
                    Error('USERID-COLUMN expected TESTUSER, got [%1]', Link."User ID");
                if Link."User ID" <> UserId() then
                    Error('USERID-COLUMN [%1] must equal UserId() [%2]', Link."User ID", UserId());
                if IsNullGuid(Link.SystemId) then
                    Error('SYSTEMID-COLUMN an AddLink row has no SystemId');
            end;

            [Test]
            procedure AddLink_CompanyOnlyForAPerCompanyTable()
            var
                Host: Record "RLC Host";
                Shared: Record "RLC Shared Host";
                Link: Record "Record Link";
            begin
                Host.Code := 'C';
                Host.Insert();
                Shared.Code := 'S';
                Shared.Insert();
                Host.AddLink('https://c.example', 'company');
                Shared.AddLink('https://s.example', 'shared');
                OnlyLinkOf(Host.RecordId(), Link);
                if Link.Company <> CompanyName() then
                    Error('COMPANY-COLUMN per-company expected [%1], got [%2]', CompanyName(), Link.Company);
                OnlyLinkOf(Shared.RecordId(), Link);
                if Link.Company <> '' then
                    Error('COMPANY-COLUMN not per company expected empty, got [%1]', Link.Company);
            end;

            [Test]
            procedure CopyLinks_KeepsTheSourceAuthor_AndTakesTheTargetCompany()
            var
                Source: Record "RLC Host";
                Target: Record "RLC Shared Host";
                Link: Record "Record Link";
            begin
                Source.Code := 'SRC';
                Source.Insert();
                Target.Code := 'DST';
                Target.Insert();
                Link.Init();
                Link."Record ID" := Source.RecordId();
                Link.URL1 := 'https://src.example';
                Link."User ID" := 'LINKAUTHOR';
                Link.Notify := true;
                Link.Company := CompanyName();
                Link.Insert(true);

                Target.CopyLinks(Source);

                OnlyLinkOf(Target.RecordId(), Link);
                if Link."User ID" <> 'LINKAUTHOR' then
                    Error('COPY-AUTHOR expected LINKAUTHOR, got [%1]', Link."User ID");
                if not Link.Notify then
                    Error('COPY-NOTIFY the copy lost Notify');
                if Link.Company <> '' then
                    Error('COPY-COMPANY expected empty on a table not per company, got [%1]', Link.Company);
                if IsNullGuid(Link.SystemId) then
                    Error('SYSTEMID-COLUMN a CopyLinks copy has no SystemId');
            end;
        }
        """);
    }

    [SkippableFact]
    public async Task RecordLinkColumns_AreFilledFromTheSkeletonSession_AndCopiesKeepTheSourceRow()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture();
        var r = await SuiteServer.RunViaServer(_root);

        Assert.True(r.ExitCode == 0, $"expected all three AL tests to pass; exit={r.ExitCode}\n{r}");
        Assert.True(r.Passed == 3, $"expected 3 passed\n{r}");
    }
}
