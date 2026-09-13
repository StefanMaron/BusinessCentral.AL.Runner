using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for #3578: BcRuntime.RecordImpl_InternalFindRecordWithoutCheckingValuesAsync
/// replaces BC's primary-key lookup and must call BC's CalcAutoCalcFieldsAsync after a found record,
/// as BC's own body does. Get, Get(RecordId), GetBySystemId and RecordRef.Get all route through it.
/// The BC-behaviour claim is pinned upstream in corpus codeunit 60910 "Test SetAutoCalcFields On Get";
/// this spawns the runner on a platform-only bundle so a regression in the replacement fails here.
/// </summary>
public class GetAutoCalcFieldsTests
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

    [SkippableFact]
    public void PrimaryKeyLookups_HonorSetAutoCalcFields()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-get-autocalcfields-3578");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3578000-0000-4000-8000-000000003578",
          "name": "GetAutoCalcFields3578",
          "publisher": "Repro3578",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63578, "to": 63579 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "GacfProbe.al"), """
        table 63578 "GACF Header"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Line Count"; Integer)
                {
                    FieldClass = FlowField;
                    CalcFormula = count("GACF Line" where("Header Code" = field("Code")));
                }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        table 63579 "GACF Line"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Header Code"; Code[20]) { }
                field(2; "Line No."; Integer) { }
            }
            keys { key(PK; "Header Code", "Line No.") { Clustered = true; } }
        }

        codeunit 63578 "GACF Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Setup(var Source: Record "GACF Header")
            var
                Hdr: Record "GACF Header";
                Ln: Record "GACF Line";
            begin
                Hdr.DeleteAll();
                Ln.DeleteAll();
                Hdr."Code" := 'A1'; Hdr.Insert();
                Hdr."Code" := 'B1'; Hdr.Insert();
                Ln."Header Code" := 'A1'; Ln."Line No." := 1; Ln.Insert();
                Ln."Header Code" := 'A1'; Ln."Line No." := 2; Ln.Insert();
                Ln."Header Code" := 'B1'; Ln."Line No." := 1; Ln.Insert();
                Source.Get('A1');
            end;

            local procedure Check(Actual: Integer; Expected: Integer; Arm: Text)
            begin
                if Actual <> Expected then
                    Error('%1: Line Count = %2, expected %3', Arm, Actual, Expected);
            end;

            [Test]
            procedure GetByKey()
            var
                Source: Record "GACF Header";
                Fresh: Record "GACF Header";
            begin
                Setup(Source);
                Fresh.SetAutoCalcFields("Line Count");
                Fresh.Get('A1');
                Check(Fresh."Line Count", 2, 'Get(key)');
            end;

            [Test]
            procedure GetByKeyWithoutAutoCalc()
            var
                Source: Record "GACF Header";
                Fresh: Record "GACF Header";
            begin
                Setup(Source);
                Fresh.Get('A1');
                Check(Fresh."Line Count", 0, 'Get(key) without SetAutoCalcFields');
            end;

            [Test]
            procedure GetByRecordId()
            var
                Source: Record "GACF Header";
                Fresh: Record "GACF Header";
            begin
                Setup(Source);
                Fresh.SetAutoCalcFields("Line Count");
                Fresh.Get(Source.RecordId);
                Check(Fresh."Line Count", 2, 'Get(RecordId)');
            end;

            [Test]
            procedure GetBySystemId()
            var
                Source: Record "GACF Header";
                Fresh: Record "GACF Header";
            begin
                Setup(Source);
                Fresh.SetAutoCalcFields("Line Count");
                Fresh.GetBySystemId(Source.SystemId);
                Check(Fresh."Line Count", 2, 'GetBySystemId');
            end;

            [Test]
            procedure RecordRefGet()
            var
                Source: Record "GACF Header";
                RecRef: RecordRef;
            begin
                Setup(Source);
                RecRef.Open(Database::"GACF Header");
                RecRef.SetAutoCalcFields(Source.FieldNo("Line Count"));
                RecRef.Get(Source.RecordId);
                Check(RecRef.Field(Source.FieldNo("Line Count")).Value, 2, 'RecordRef.Get');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0, $"runner exited {exitCode}:\n{output}");
        Assert.DoesNotContain("FAIL", output);
        foreach (var name in new[] { "GetByKey", "GetByKeyWithoutAutoCalc", "GetByRecordId", "GetBySystemId", "RecordRefGet" })
            Assert.Contains($"PASS  Codeunit63578.{name} ", output);
    }
}
