// MissingTestDataSeededSingletonTests — the proving tests for #2277: a TestField failure on a
// setup table whose ONE row the install seeding created blank.
//
// Asserted on the printed per-test block, like MissingTestDataDiagnosisTests, because the claim
// is about what a developer reads. Runner output, not BC behaviour, so it stays out of the corpus.
//
// Route for "the install seed inserted one blank singleton row" without the Base Application
// floor (.claude/rules/no-base-app-in-csharp-tests.md): the fixture app's own Subtype=Install
// codeunit inserts the rows. TestExecutor runs a bundle's own install triggers before
// CaptureInstallBaseline, so those rows are in the install baseline exactly as Base
// Application's Company-Initialize rows are — the property the diagnosis reads.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class MissingTestDataSeededSingletonTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const int SeededBlankTableId = 62451;
    private const int SeededBlankOtherTableId = 62454;

    private readonly string _root;

    public MissingTestDataSeededSingletonTests()
    {
        _root = TestScratch.Dir("al-runner-seeded-singleton");
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c4e1a7d2-5b83-4f19-a6c2-3d8e9f0b1a24",
          "name": "Seeded Singleton Diagnosis Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "27.0.0.0",
          "idRanges": [ { "from": 62450, "to": 62459 } ],
          "runtime": "17.0",
          "target": "Cloud"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "SeedTables.al"), """
        table 62451 "Seeded Blank Setup"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Primary Key"; Code[10]) { }
                field(2; "Invoice Nos."; Code[20]) { }
                field(3; Description; Text[50]) { }
            }
            keys { key(PK; "Primary Key") { Clustered = true; } }
        }

        table 62452 "Seeded Filled Setup"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Primary Key"; Code[10]) { }
                field(2; "Invoice Nos."; Code[20]) { }
            }
            keys { key(PK; "Primary Key") { Clustered = true; } }
        }

        table 62453 "Test Owned Setup"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Primary Key"; Code[10]) { }
                field(2; "Invoice Nos."; Code[20]) { }
            }
            keys { key(PK; "Primary Key") { Clustered = true; } }
        }

        table 62454 "Seeded Blank Other Setup"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Primary Key"; Code[10]) { }
                field(2; "Credit Memo Nos."; Code[20]) { }
            }
            keys { key(PK; "Primary Key") { Clustered = true; } }
        }

        codeunit 62450 "Seeded Singleton Install"
        {
            Subtype = Install;

            // The shape Base Application's setup initialisation has: create the singleton only
            // when it is not there, so a --test-data backup that already supplied it wins.
            trigger OnInstallAppPerCompany()
            var
                SeededBlank: Record "Seeded Blank Setup";
                SeededFilled: Record "Seeded Filled Setup";
                SeededBlankOther: Record "Seeded Blank Other Setup";
            begin
                if not SeededBlank.Get() then begin
                    SeededBlank.Init();
                    SeededBlank.Insert();
                end;
                if not SeededFilled.Get() then begin
                    SeededFilled.Init();
                    SeededFilled."Invoice Nos." := 'P-INV';
                    SeededFilled.Insert();
                end;
                if not SeededBlankOther.Get() then begin
                    SeededBlankOther.Init();
                    SeededBlankOther.Insert();
                end;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "SeedTests.Codeunit.al"), """
        codeunit 62455 "Seeded Singleton Diag Tests"
        {
            Subtype = Test;

            [Test]
            procedure SeededBlankSingleton_TestField()
            var
                Setup: Record "Seeded Blank Setup";
            begin
                Setup.Get();
                Setup.TestField("Invoice Nos.");
            end;

            [Test]
            procedure SeededBlankOther_TestField()
            var
                Setup: Record "Seeded Blank Other Setup";
            begin
                Setup.Get();
                Setup.TestField("Credit Memo Nos.");
            end;

            [Test]
            procedure TestInsertedRow_ThenBlanked_TestField()
            var
                Setup: Record "Test Owned Setup";
            begin
                Setup.Init();
                Setup."Invoice Nos." := 'T-INV';
                Setup.Insert();
                Setup."Invoice Nos." := '';
                Setup.Modify();
                Setup.Get();
                Setup.TestField("Invoice Nos.");
            end;

            [Test]
            procedure SeededValueBlankedByTest_TestField()
            var
                Setup: Record "Seeded Filled Setup";
            begin
                Setup.Get();
                Setup."Invoice Nos." := '';
                Setup.Modify();
                Setup.Get();
                Setup.TestField("Invoice Nos.");
            end;

            [Test]
            procedure ReportSeededBlankDescription()
            var
                Setup: Record "Seeded Blank Setup";
            begin
                Setup.Get();
                Error('seeded-blank description=[%1]', Setup.Description);
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(IDictionary<string, string>? env = null,
                                                params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>One test's FAIL/ERROR header plus its continuation lines.</summary>
    private static string BlockFor(string output, string testMethod)
    {
        var block = new StringBuilder();
        var inBlock = false;
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var isHeader = line.StartsWith("FAIL ", StringComparison.Ordinal)
                        || line.StartsWith("PASS ", StringComparison.Ordinal)
                        || line.StartsWith("ERROR", StringComparison.Ordinal)
                        || line.StartsWith("SKIP ", StringComparison.Ordinal);
            if (isHeader)
            {
                if (inBlock) break;
                inBlock = line.Contains($".{testMethod} (", StringComparison.Ordinal);
                if (inBlock) block.AppendLine(line);
                continue;
            }
            if (inBlock) block.AppendLine(line);
        }
        Assert.True(block.Length > 0,
            $"no reported block for '{testMethod}' — the fixture did not run as expected. Full output:\n{output}");
        return block.ToString();
    }

    /// <summary>
    /// Flag off. The two seeded-blank singletons are explained, each with its own table id and
    /// field; the two rows whose blank the TEST produced — a row it inserted itself, and a
    /// seeded value it cleared — get nothing, although their failures are the same exception
    /// with the same message shape on a one-row table.
    /// </summary>
    [SkippableFact]
    public void SeededBlankSingleton_IsExplained_AndATestProducedBlankIsNot()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();
        Assert.NotEqual(0, exit);

        var seeded = BlockFor(output, "SeededBlankSingleton_TestField");
        Assert.Contains("NavTestFieldException: Invoice Nos. must have a value in Seeded Blank Setup",
            seeded, StringComparison.Ordinal);
        Assert.Contains($"[test-data] 'Seeded Blank Setup' (table {SeededBlankTableId}) holds only the row "
            + "the runner's install seeding created, and 'Invoice Nos.' is still blank in it, so this "
            + "failure may be missing setup data", seeded, StringComparison.Ordinal);
        Assert.Contains("Pass --test-data", seeded, StringComparison.Ordinal);
        Assert.Contains("or set 'Invoice Nos.' in the test's own setup", seeded, StringComparison.Ordinal);

        var other = BlockFor(output, "SeededBlankOther_TestField");
        Assert.Contains($"[test-data] 'Seeded Blank Other Setup' (table {SeededBlankOtherTableId}) holds only "
            + "the row the runner's install seeding created, and 'Credit Memo Nos.' is still blank in it",
            other, StringComparison.Ordinal);
        Assert.DoesNotContain($"table {SeededBlankTableId}", other, StringComparison.Ordinal);

        var testInserted = BlockFor(output, "TestInsertedRow_ThenBlanked_TestField");
        Assert.Contains("NavTestFieldException: Invoice Nos. must have a value in Test Owned Setup",
            testInserted, StringComparison.Ordinal);
        Assert.DoesNotContain("[test-data]", testInserted, StringComparison.Ordinal);

        var testBlanked = BlockFor(output, "SeededValueBlankedByTest_TestField");
        Assert.Contains("NavTestFieldException: Invoice Nos. must have a value in Seeded Filled Setup",
            testBlanked, StringComparison.Ordinal);
        Assert.DoesNotContain("[test-data]", testBlanked, StringComparison.Ordinal);

        // The seeded row really is the install seed's: its Description is blank.
        Assert.Contains("seeded-blank description=[]", BlockFor(output, "ReportSeededBlankDescription"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Flag on. The fake backup supplies "Seeded Blank Setup" with a blank Invoice Nos. — the
    /// install trigger's Get() loads it, so the row is the company's, and its blank is not
    /// missing data: no explanation. "Seeded Blank Other Setup" is not in the backup, so its
    /// row is still the seed's, and the sentence says --test-data is on and why it did not help.
    /// </summary>
    [SkippableFact]
    public void WithTestDataOn_ABackupRowIsNotBlamed_AndAStillSeededRowSaysWhy()
    {
        TestArtifacts.SkipIfMissing();

        var backup = Path.Combine(_root, "BusinessCentral-W1.bak");
        File.WriteAllBytes(backup, new byte[256]);
        var reader = Path.Combine(_root, "bcbak");
        File.WriteAllText(reader, FakeReaderScript());
        File.SetUnixFileMode(reader,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (output, _) = RunRunner(
            new Dictionary<string, string> { ["AL_RUNNER_BCBAK"] = reader },
            $"--test-data={backup}", "--test-data-company", "CRONUS");

        // Non-vacuity: the backup's row is the one in the store, not the install seed's.
        Assert.Contains("seeded-blank description=[FROM-BACKUP]",
            BlockFor(output, "ReportSeededBlankDescription"), StringComparison.Ordinal);

        var fromBackup = BlockFor(output, "SeededBlankSingleton_TestField");
        Assert.Contains("NavTestFieldException: Invoice Nos. must have a value in Seeded Blank Setup",
            fromBackup, StringComparison.Ordinal);
        Assert.DoesNotContain("[test-data]", fromBackup, StringComparison.Ordinal);

        var stillSeeded = BlockFor(output, "SeededBlankOther_TestField");
        Assert.Contains($"[test-data] 'Seeded Blank Other Setup' (table {SeededBlankOtherTableId}) holds only "
            + "the row the runner's install seeding created, and 'Credit Memo Nos.' is still blank in it "
            + "although --test-data is on: the plan built from", stillSeeded, StringComparison.Ordinal);
        Assert.DoesNotContain("Pass --test-data", stillSeeded, StringComparison.Ordinal);

        Assert.DoesNotContain("[test-data]", BlockFor(output, "TestInsertedRow_ThenBlanked_TestField"),
            StringComparison.Ordinal);
    }

    private static string FakeReaderScript() =>
        "#!/bin/sh\n"
        + "cmd=\"$1\"\n"
        + "case \"$cmd\" in\n"
        + "  companies) echo 'CRONUS' ;;\n"
        + $"  tables) printf '%s\\n' '   1 Table\tCRONUS\tSeeded Blank Setup\t{SeededBlankTableId} \"Seeded Blank Setup\" (Seeded Singleton Diagnosis Fixture)' ;;\n"
        + "  read) echo '[{\"Primary Key\": \"\", \"Invoice Nos.\": \"\", \"Description\": \"FROM-BACKUP\"}]' ;;\n"
        + "esac\n";
}
