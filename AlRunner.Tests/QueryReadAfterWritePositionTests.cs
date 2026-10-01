using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #5133 — runner mechanism for a query re-read after a write invalidates its result set.
/// NavQuery.GetNextRowAsync re-reads from its last QUERY row; RecordPatches.FindQueryFromPosition
/// serves that positioned find instead of handing the query row to BC's table provider.
///
/// What BC returns is the corpus's claim (codeunit 68530, corpus PR #523). This pins the runner's
/// own half: the re-read resumes after the starting row rather than casting, and a positioning
/// column the runner cannot faithfully filter on (an aggregate) refuses with its named reason.
/// </summary>
public class QueryReadAfterWritePositionTests
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
        var root = TestScratch.Dir("al-runner-query-reread-5133");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "5133a1b2-c3d4-4e5f-8a9b-000000005133",
          "name": "QRP 5133 Repro",
          "publisher": "Repro5133",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 51330, "to": 51339 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Qrp.al"), """
        table 51330 "QRP Entry"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Cust No."; Code[20]) { }
                field(3; Amount; Decimal) { }
                field(4; Processed; Boolean) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        query 51331 "QRP Entries"
        {
            QueryType = Normal;
            OrderBy = ascending(EntryNo);
            elements
            {
                dataitem(Entry; "QRP Entry")
                {
                    column(EntryNo; "Entry No.") { }
                    column(Amount; Amount) { }
                }
            }
        }

        table 51334 "QRP Cust"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Name; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        query 51335 "QRP Joined"
        {
            QueryType = Normal;
            OrderBy = descending(EntryNo);
            elements
            {
                dataitem(Entry; "QRP Entry")
                {
                    column(EntryNo; "Entry No.") { }
                    dataitem(Cust; "QRP Cust")
                    {
                        DataItemLink = "No." = Entry."Cust No.";
                        SqlJoinType = InnerJoin;
                        column(CustName; Name) { }
                    }
                }
            }
        }

        query 51332 "QRP Totals By Amount"
        {
            QueryType = Normal;
            OrderBy = descending(TotalAmount);
            elements
            {
                dataitem(Entry; "QRP Entry")
                {
                    column(CustNo; "Cust No.") { }
                    column(TotalAmount; Amount) { Method = Sum; }
                }
            }
        }

        codeunit 51333 "QRP 5133 Tests"
        {
            Subtype = Test;

            local procedure Initialize()
            var
                Entry: Record "QRP Entry";
            begin
                Entry.DeleteAll();
                Entry.Init(); Entry."Entry No." := 1; Entry."Cust No." := 'C1'; Entry.Amount := 10; Entry.Insert();
                Entry.Init(); Entry."Entry No." := 2; Entry."Cust No." := 'C1'; Entry.Amount := 20; Entry.Insert();
                Entry.Init(); Entry."Entry No." := 3; Entry."Cust No." := 'C2'; Entry.Amount := 30; Entry.Insert();
            end;

            local procedure InitializeCustomers()
            var
                Cust: Record "QRP Cust";
            begin
                Cust.DeleteAll();
                Cust.Init(); Cust."No." := 'C1'; Cust.Name := 'One'; Cust.Insert();
                Cust.Init(); Cust."No." := 'C2'; Cust.Name := 'Two'; Cust.Insert();
            end;

            // A join with TopNumberOfRows(2) and a write in the loop: the re-read is positioned
            // before TOP is taken, so the loop still reads two rows in total (corpus 68534 pins
            // the single-dataitem twin on BC).
            [Test]
            procedure JoinTop2_ModifyInLoop_StillReadsTwoRows()
            var
                Joined: Query "QRP Joined";
                Seen: Text;
            begin
                Initialize();
                InitializeCustomers();
                Joined.TopNumberOfRows(2);
                Joined.Open();
                while Joined.Read() do begin
                    if StrLen(Seen) > 40 then
                        Error('QRP-JOINTOP the re-read restarted from the top: %1', Seen);
                    Seen += Format(Joined.EntryNo) + ';';
                    MarkProcessed(Joined.EntryNo);
                end;
                Joined.Close();
                if Seen <> '3;2;' then
                    Error('QRP-JOINTOP expected 3;2; got %1', Seen);
            end;

            local procedure MarkProcessed(EntryNo: Integer)
            var
                Entry: Record "QRP Entry";
            begin
                Entry.Get(EntryNo);
                Entry.Processed := true;
                Entry.Modify();
            end;

            // The re-read resumes strictly after the last row: entries 2 and 3 once each, and
            // the row after a re-read carries its own values.
            [Test]
            procedure ModifyInLoop_ResumesAfterTheLastRow()
            var
                Entries: Query "QRP Entries";
                Seen: Text;
            begin
                Initialize();
                Entries.Open();
                while Entries.Read() do begin
                    if StrLen(Seen) > 40 then
                        Error('QRP-RESUME the re-read restarted from the top: %1', Seen);
                    Seen += Format(Entries.EntryNo) + '=' + Format(Entries.Amount) + ';';
                    MarkProcessed(Entries.EntryNo);
                end;
                Entries.Close();
                if Seen <> '1=10;2=20;3=30;' then
                    Error('QRP-RESUME expected 1=10;2=20;3=30; got %1', Seen);
            end;

            // Positioning on an aggregated column has no faithful filter here: it must refuse
            // with its named reason, never cast and never re-read from the top.
            [Test]
            procedure ModifyInLoop_OrderedByAggregate_RefusesByName()
            var
                Totals: Query "QRP Totals By Amount";
                Reads: Integer;
            begin
                Initialize();
                Totals.Open();
                while Totals.Read() do begin
                    Reads += 1;
                    if Reads > 5 then
                        Error('QRP-LOOP the re-read restarted from the top');
                    MarkProcessed(1);
                end;
                Totals.Close();
            end;
        }
        """);

        return root;
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(3, 3)]
    public void APositioningSlotOutsideTheRow_RefusesAsAShapeGap(int slot, int fieldCount)
    {
        var ex = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
            () => AlRunner.Patches.RecordPatches.RequirePositioningSlotInRow(slot, fieldCount));
        Assert.Contains($"positioning slot {slot} is outside the projected row", ex.Message);
    }

    [Fact]
    public void APositioningSlotInsideTheRow_IsAccepted()
    {
        AlRunner.Patches.RecordPatches.RequirePositioningSlotInRow(0, 3);
        AlRunner.Patches.RecordPatches.RequirePositioningSlotInRow(2, 3);
    }

    [SkippableFact]
    public void QueryReRead_ResumesAfterTheStartingRow_AndRefusesAnAggregatePosition()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner(WriteBundle());

        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        Assert.DoesNotContain("InvalidCastException", output);
        Assert.DoesNotContain("QRP-RESUME", output);
        Assert.DoesNotContain("QRP-LOOP", output);
        Assert.DoesNotContain("QRP-JOINTOP", output);
        Assert.Contains("PASS  Codeunit51333.JoinTop2_ModifyInLoop_StillReadsTwoRows", output);
        Assert.Contains("PASS  Codeunit51333.ModifyInLoop_ResumesAfterTheLastRow", output);
        Assert.Contains("query-reread-position-on-aggregated-column", output);
        Assert.Contains("2P/1F/0E", output);
    }
}
