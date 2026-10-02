// RenameWriteNoteTests — issue #4877. An AL Rename never reached ALDatabasePatches.NoteRecordWrite:
// the write note was prepended to NavRecord.ALRenameAsync, but the ALRename the AL compiler binds
// goes Rename(DataError, bool, bool, NavValue[]) -> RenameAsync(same) and never through it. So a
// Rename inside asserterror was not rolled back, and it did not open a write transaction or move
// Database.LastUsedRowVersion. The note now sits on the RenameAsync funnel.
//
// Runner-side mechanism test: it pins which Ncl entry point the runner's write note must reach.
// The BC-observable half (a rolled-back Rename) is also a corpus test, cited in the PR.
using Xunit;

namespace AlRunner.Tests;

public class RenameWriteNoteTests
{
    private const int BaseId = 62850;

    private static void WriteFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "6a4877e0-0000-4c38-9e25-000000004877",
          "name": "IT4877 Rename Write Note",
          "publisher": "IssueTest4877",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{BaseId}}, "to": {{BaseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), $$"""
        table {{BaseId}} "IT4877 W"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit {{BaseId + 1}} "IT4877 Rename Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                W: Record "IT4877 W";
            begin
                W.DeleteAll();
                W."Code" := 'A';
                W.Insert();
                Commit();
            end;

            [Test]
            procedure RenameInsideAssertError_IsRolledBack()
            var
                W: Record "IT4877 W";
            begin
                Seed();
                asserterror begin
                    W.Get('A');
                    W.Rename('B');
                    Error('boom');
                end;
                if GetLastErrorText() <> 'boom' then
                    Error('unexpected error <%1>', GetLastErrorText());
                if not W.Get('A') then
                    Error('Rename was not rolled back: A is missing');
                if W.Get('B') then
                    Error('Rename was not rolled back: B exists');
            end;

            [Test]
            procedure RenameCommitted_IsKept()
            var
                W: Record "IT4877 W";
            begin
                Seed();
                W.Get('A');
                W.Rename('B');
                Commit();
                asserterror Error('boom');
                if W.Get('A') then
                    Error('a committed Rename came undone: A exists');
                if not W.Get('B') then
                    Error('a committed Rename came undone: B is missing');
            end;

            [Test]
            procedure Rename_MovesLastUsedRowVersion()
            var
                W: Record "IT4877 W";
                Before: BigInteger;
            begin
                Seed();
                Before := Database.LastUsedRowVersion();
                W.Get('A');
                W.Rename('B');
                if Database.LastUsedRowVersion() <= Before then
                    Error('LastUsedRowVersion did not advance on Rename (%1 -> %2)', Before, Database.LastUsedRowVersion());
            end;

            [Test]
            procedure DeleteInsideAssertError_IsRolledBack_Control()
            var
                W: Record "IT4877 W";
            begin
                Seed();
                asserterror begin
                    W.Get('A');
                    W.Delete();
                    Error('boom');
                end;
                if not W.Get('A') then
                    Error('Delete was not rolled back: A is missing');
            end;
        }
        """);
    }

    [SkippableFact]
    public void ARename_IsNotedAsAWrite_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-4877-rename");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var cache = Path.Combine(root, "cache");
            foreach (var pass in new[] { "cold", "warm" })
            {
                var (output, exit) = AllObjPopulateCostTests.RunRunner(cache, app);
                Assert.True(exit == 0 && output.Contains("4P/0F/0E"),
                    $"{pass} run: expected all 4 rename tests to pass (exit {exit}), got:\n{output}");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
