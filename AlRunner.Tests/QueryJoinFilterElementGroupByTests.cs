using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #5145 — runner-mechanism guard for the JOIN path's GROUP BY: a filter-only column
/// (an AL <c>filter()</c> element) is not a group key (JoinExecutor.BuildGroupedRows), and the
/// query's WHERE filters reach the executor and are applied to the joined rows before grouping
/// (RecordPatches.BuildJoinWhereFilters → JoinExecutor.ApplyWhereFilters).
///
/// The BC-behaviour claim lives upstream in corpus codeunit 68600 (corpus PR #524). This class
/// pins the runner's own pipeline. Each group holds rows with different dates, and the
/// filtered test gives header A a row outside the range inserted FIRST, so a filter checked
/// against a group's first row drops A instead of narrowing it.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryJoinFilterElementGroupByTests
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
        var root = TestScratch.Dir("al-runner-query-join-filter-element-5145");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c1f0a5e2-5145-4d7b-9a61-000000005145",
          "name": "QJF 5145 Repro",
          "publisher": "Repro5145",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 75140, "to": 75150 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Qjf5145.al"), """
        table 75140 "QJF5145 Header"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "No."; Code[20]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        table 75141 "QJF5145 Entry"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Header No."; Code[20]) { }
                field(3; "Posting Date"; Date) { }
                field(4; Amount; Decimal) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        query 75142 "QJF5145 Totals"
        {
            QueryType = Normal;
            OrderBy = ascending(HeaderNo);
            elements
            {
                dataitem(Header; "QJF5145 Header")
                {
                    column(HeaderNo; "No.") { }
                    dataitem(Entry; "QJF5145 Entry")
                    {
                        DataItemLink = "Header No." = Header."No.";
                        SqlJoinType = InnerJoin;
                        column(TotalAmount; Amount) { Method = Sum; }
                        column(EntryCount) { Method = Count; }
                        filter(DateFilter; "Posting Date") { }
                    }
                }
            }
        }

        codeunit 75143 "QJF5145 Tests"
        {
            Subtype = Test;

            local procedure Init()
            var
                Header: Record "QJF5145 Header";
                Entry: Record "QJF5145 Entry";
            begin
                Entry.DeleteAll();
                Header.DeleteAll();
                Header.Init(); Header."No." := 'A'; Header.Insert();
                Header.Init(); Header."No." := 'B'; Header.Insert();
                Entry.Init(); Entry."Entry No." := 1; Entry."Header No." := 'A'; Entry."Posting Date" := 20260705D; Entry.Amount := 1000; Entry.Insert();
                Entry.Init(); Entry."Entry No." := 2; Entry."Header No." := 'A'; Entry."Posting Date" := 20260610D; Entry.Amount := 100; Entry.Insert();
                Entry.Init(); Entry."Entry No." := 3; Entry."Header No." := 'A'; Entry."Posting Date" := 20260620D; Entry.Amount := 50; Entry.Insert();
                Entry.Init(); Entry."Entry No." := 4; Entry."Header No." := 'B'; Entry."Posting Date" := 20260705D; Entry.Amount := 9; Entry.Insert();
            end;

            [Test]
            procedure FilterElement_IsNotAGroupKey()
            var
                Q: Query "QJF5145 Totals";
                Rows: Text;
            begin
                Init();
                Q.Open();
                while Q.Read() do
                    Rows += StrSubstNo('%1:%2:%3;', Q.HeaderNo, Format(Q.TotalAmount, 0, 9), Q.EntryCount);
                Q.Close();
                if Rows <> 'A:1150:3;B:9:1;' then
                    Error('expected one group per header A:1150:3;B:9:1; got %1', Rows);
            end;

            [Test]
            procedure FilterElement_FiltersRowsBeforeGrouping()
            var
                Q: Query "QJF5145 Totals";
                Rows: Text;
            begin
                Init();
                Q.SetRange(DateFilter, 20260601D, 20260630D);
                Q.Open();
                while Q.Read() do
                    Rows += StrSubstNo('%1:%2:%3;', Q.HeaderNo, Format(Q.TotalAmount, 0, 9), Q.EntryCount);
                Q.Close();
                if Rows <> 'A:150:2;' then
                    Error('expected only A''s June rows A:150:2; got %1', Rows);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void JoinWithFilterElement_GroupsByOutputColumnsAndFiltersBeforeAggregating()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exitCode) = RunRunner(WriteBundle());

        Assert.DoesNotContain("COMPILE FAIL", output);
        Assert.True(exitCode == 0, $"runner exit {exitCode}:\n{output}");
        Assert.Contains("2P/0F/0E", output);
    }
}
