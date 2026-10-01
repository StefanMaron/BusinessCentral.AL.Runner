// RecordLinkStoreEndToEndTests — issue #4944.
//
// RUNNER-MECHANISM test: the Record Link store (RecordPatches.RecordLinkTable.cs) reads its
// rows back out of BC's in-memory provider through reflection binds, end to end through a real
// bundle run. It pins that the binds READ real rows — non-empty, per record, surviving a
// delete of another record's links — and that a store holding nothing answers "no links"
// without refusing. A failed bind refusing instead of reading as zero is pinned by
// RecordLinkRowReaderTests. What BC does with links is measured upstream (corpus codeunit
// 60777, "Test Record Link Table"). Runs through the shared suite server
// (docs/shared-cli-server.md#suite-server).
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordLinkStoreEndToEndTests : IDisposable
{
    private readonly string _root;

    public RecordLinkStoreEndToEndTests()
    {
        _root = TestScratch.Dir("al-runner-record-link-store-e2e");
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
          "id": "0b4944a1-4944-4944-4944-494449444944",
          "name": "Record Link Store E2E Repro",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 94940, "to": 94949 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Fixture.al"), """
        table 94940 "RLSE Item"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys
            {
                key(PK; "Code") { Clustered = true; }
            }
        }

        codeunit 94941 "RLSE Tests"
        {
            Subtype = Test;

            [Test]
            procedure AddedLinks_AreRowsTheLinkSurfaceReadsBack()
            var
                Item: Record "RLSE Item";
                Other: Record "RLSE Item";
                Link: Record "Record Link";
                FirstId: Integer;
            begin
                Item.Code := 'A';
                Item.Insert();
                Other.Code := 'B';
                Other.Insert();

                FirstId := Item.AddLink('https://a.example', 'first');
                Item.AddLink('https://b.example', 'second');
                Other.AddLink('https://c.example', 'other');

                if not Item.HasLinks() then
                    Error('HasLinks must read the two stored rows of A back');
                Link.SetRange("Record ID", Item.RecordId());
                if Link.Count() <> 2 then
                    Error('expected 2 Record Link rows for A, got %1', Link.Count());

                Item.DeleteLink(FirstId);
                if Link.Count() <> 1 then
                    Error('after DeleteLink expected 1 row for A, got %1', Link.Count());

                Item.DeleteLinks();
                if Item.HasLinks() then
                    Error('DeleteLinks must remove every link of A');
                if not Other.HasLinks() then
                    Error('the link of B must survive DeleteLinks on A');
            end;
        }

        // A codeunit of its own: rows persist across test methods within one codeunit, so the
        // "nothing stored" case must not share one with the test that stores links.
        codeunit 94942 "RLSE Empty Store Tests"
        {
            Subtype = Test;

            [Test]
            procedure NoLinksStored_HasLinksIsFalse_WithoutRefusing()
            var
                Item: Record "RLSE Item";
                Link: Record "Record Link";
            begin
                if not Link.IsEmpty() then
                    Error('expected an empty Record Link table, got %1 rows', Link.Count());
                Item.Code := 'Z';
                Item.Insert();
                if Item.HasLinks() then
                    Error('a record with no stored link must answer HasLinks = false');
                Item.DeleteLinks();
            end;
        }
        """);
    }

    [SkippableFact]
    public async Task RecordLinkStore_ReadsStoredRowsBack_AndAnEmptyStoreAnswersNoLinks()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture();
        var result = await SuiteServer.RunViaServer(_root);

        Assert.True(result.ExitCode == 0, $"expected both AL tests to pass; exit={result.ExitCode}\n{result}");
        result.AssertCounts(passed: 2, failed: 0, errors: 0);
        result.AssertOutputDoesNotContain("bc-shape-gap");
    }
}
