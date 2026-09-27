// RenameWriteGuardTests — issue #4879. The All Profile and page-background-task write guards
// were prepended to NavRecord.ALRenameAsync, which the ALRename the AL compiler binds never calls
// (#4877), so a plain Rec.Rename was never refused. Both now sit on the RenameAsync funnel.
//
// Runner-side mechanism test: it pins which Ncl entry point the runner's guards must reach. The
// BC-observable halves are corpus tests (60907 for All Profile, the page background task write
// tests for the worker), cited in the PR.
using Xunit;

namespace AlRunner.Tests;

public class RenameWriteGuardTests
{
    private const int BaseId = 62870;

    private static void WriteFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "6a4879e0-0000-4c38-9e25-000000004879",
          "name": "IT4879 Rename Write Guards",
          "publisher": "IssueTest4879",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{BaseId}}, "to": {{BaseId + 19}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), $$"""
        page {{BaseId}} "IT4879 RC"
        {
            PageType = RoleCenter;
        }

        profile "IT4879 PROFILE"
        {
            Caption = 'IT4879 Profile';
            RoleCenter = "IT4879 RC";
        }

        table {{BaseId + 1}} "IT4879 Row"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Name; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page {{BaseId + 2}} "IT4879 Card"
        {
            PageType = Card;
            SourceTable = "IT4879 Row";
            layout { area(Content) { field("No."; Rec."No.") { ApplicationArea = All; } } }
        }

        codeunit {{BaseId + 3}} "IT4879 Rename Worker"
        {
            trigger OnRun()
            var
                Row: Record "IT4879 Row";
                Params: Dictionary of [Text, Text];
                RowNo: Text;
            begin
                Params := Page.GetBackgroundParameters();
                Params.Get('No', RowNo);
                Row.Get(CopyStr(RowNo, 1, MaxStrLen(Row."No.")));
                Row.Rename(CopyStr(RowNo + '-R', 1, MaxStrLen(Row."No.")));
            end;
        }

        codeunit {{BaseId + 4}} "IT4879 Guard Tests"
        {
            Subtype = Test;

            [Test]
            procedure RenameAppOwnedProfile_IsRefused()
            var
                AllProfile: Record "All Profile";
                ThisModule: ModuleInfo;
            begin
                NavApp.GetCurrentModuleInfo(ThisModule);
                if not AllProfile.Get(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 PROFILE') then
                    Error('the declared profile must be an All Profile row');

                asserterror AllProfile.Rename(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 RENAMED');
                if StrPos(GetLastErrorText(), 'IT4879 RENAMED') = 0 then
                    Error('REFUSAL: expected the refusal to name the new Profile ID, got <%1>', GetLastErrorText());

                Clear(AllProfile);
                if not AllProfile.Get(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 PROFILE') then
                    Error('a refused Rename must leave the profile under its old key');
                if AllProfile.Get(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 RENAMED') then
                    Error('a refused Rename must not create the new key');
            end;

            [Test]
            procedure RenameAppOwnedProfileToItsOwnKey_Succeeds()
            var
                AllProfile: Record "All Profile";
                ThisModule: ModuleInfo;
            begin
                NavApp.GetCurrentModuleInfo(ThisModule);
                AllProfile.Get(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 PROFILE');

                // Not a key change, so BC's IsRecordKeyChange guard never reaches its refusal.
                AllProfile.Rename(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 PROFILE');

                Clear(AllProfile);
                if not AllProfile.Get(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 PROFILE') then
                    Error('a Rename onto its own key must leave the profile where it is');
            end;

            [Test]
            procedure RenameTenantProfileOntoAnAppId_IsRefused()
            var
                AllProfile: Record "All Profile";
                ThisModule: ModuleInfo;
                EmptyGuid: Guid;
            begin
                NavApp.GetCurrentModuleInfo(ThisModule);
                AllProfile.Init();
                AllProfile.Scope := AllProfile.Scope::Tenant;
                AllProfile."Profile ID" := 'IT4879 TENANT X';
                AllProfile."Role Center ID" := Page::"IT4879 RC";
                AllProfile.Enabled := false;
                AllProfile.Insert();
                // asserterror rolls back to the last commit point, which would take the Insert too.
                Commit();

                // The App ID the row is renamed TO is an installed app's: BC judges the new key.
                asserterror AllProfile.Rename(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 TENANT X');
                if StrPos(GetLastErrorText(), 'part of an installed app') = 0 then
                    Error('REFUSAL: expected the installed-app refusal, got <%1>', GetLastErrorText());

                Clear(AllProfile);
                if not AllProfile.Get(AllProfile.Scope::Tenant, EmptyGuid, 'IT4879 TENANT X') then
                    Error('a refused Rename must leave the tenant profile under its old key');
                if AllProfile.Get(AllProfile.Scope::Tenant, ThisModule.Id(), 'IT4879 TENANT X') then
                    Error('a refused Rename must not create the new key');
            end;

            [Test]
            procedure RenameTenantOwnedProfile_Succeeds()
            var
                AllProfile: Record "All Profile";
                EmptyGuid: Guid;
            begin
                AllProfile.Init();
                AllProfile.Scope := AllProfile.Scope::Tenant;
                AllProfile."Profile ID" := 'IT4879 TENANT';
                AllProfile."Role Center ID" := Page::"IT4879 RC";
                AllProfile.Enabled := false;
                AllProfile.Insert();

                AllProfile.Rename(AllProfile.Scope::Tenant, EmptyGuid, 'IT4879 TENANT R');

                Clear(AllProfile);
                if not AllProfile.Get(AllProfile.Scope::Tenant, EmptyGuid, 'IT4879 TENANT R') then
                    Error('a tenant-owned profile must be renamable');
                if AllProfile.Get(AllProfile.Scope::Tenant, EmptyGuid, 'IT4879 TENANT') then
                    Error('the renamed tenant profile must be gone under its old key');
            end;

            [Test]
            procedure BackgroundTaskWorkerRename_IsRefused()
            var
                Row: Record "IT4879 Row";
                Card: TestPage "IT4879 Card";
                Params: Dictionary of [Text, Text];
            begin
                Row.DeleteAll();
                Row."No." := 'WR-REN';
                Row.Name := 'Original';
                Row.Insert();
                Commit();

                Card.OpenView();
                Params.Add('No', 'WR-REN');
                if TryRunRenameTask(Card, Params) then
                    Error('a page background task worker''s Rename() must be refused');
                if StrPos(GetLastErrorText(), 'Sorry, the current permissions prevented the action') = 0 then
                    Error('REFUSAL: expected the read-only-session refusal, got <%1>', GetLastErrorText());
                Card.Close();

                if not Row.Get('WR-REN') then
                    Error('a refused Rename() must leave the row under its old key');
                if Row.Get('WR-REN-R') then
                    Error('a refused Rename() must not create the new key');
            end;

            [TryFunction]
            local procedure TryRunRenameTask(var Card: TestPage "IT4879 Card"; Params: Dictionary of [Text, Text])
            var
                Results: Dictionary of [Text, Text];
            begin
                Results := Card.RunPageBackgroundTask(Codeunit::"IT4879 Rename Worker", Params, false);
            end;
        }
        """);
    }

    [SkippableFact]
    public void ARename_ReachesTheWriteGuards_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-4879-rename-guards");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var cache = Path.Combine(root, "cache");
            foreach (var pass in new[] { "cold", "warm" })
            {
                var (output, exit) = AllObjPopulateCostTests.RunRunner(cache, app);
                Assert.True(exit == 0 && output.Contains("5P/0F/0E"),
                    $"{pass} run: expected all 5 rename-guard tests to pass (exit {exit}), got:\n{output}");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
