using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Build a small library .app via alc.exe with an AutoIncrement Integer PK,
/// then run the pipeline with that table referenced ONLY through --packages
/// (no library AL source in the input paths). This forces the auto-stub path
/// in <see cref="AlRunnerPipeline"/>.RenderTableStubFromSymbols.
///
/// Tests skip when alc.exe is unavailable, matching DuplicatePackageTests.
/// </summary>
[Collection("Pipeline")]
public class AutoStubAutoIncrementTests
{
    private static readonly string? AlcPath = FindAlcPath();

    private static string? FindAlcPath()
    {
        var envPath = Environment.GetEnvironmentVariable("AL_COMPILER_PATH");
        if (envPath != null && File.Exists(envPath)) return envPath;

        var vscodePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions");
        if (!Directory.Exists(vscodePath)) return null;

        var (platformDir, fileName) = OperatingSystem.IsWindows()
            ? ("win32", "alc.exe")
            : ("linux", "alc");

        foreach (var extDir in Directory.GetDirectories(vscodePath, "ms-dynamics-smb.al-*"))
        {
            var candidate = Path.Combine(extDir, "bin", platformDir, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public async Task AutoStub_PreservesAutoIncrement_TwoInsertsGetDistinctIds()
    {
        if (AlcPath == null) return;

        await RunAutoStubScenario(async (testDir, pkgDir) =>
        {
            var libGuid = Guid.NewGuid();
            var testGuid = Guid.NewGuid();
            await BuildLibraryApp(libGuid, pkgDir);
            WriteTestAppJson(testDir, testGuid, libGuid);

            // BC fires AutoIncrement on Insert regardless of the RunTrigger overload
            // (https://learn.microsoft.com/dynamics365/business-central/dev-itpro/developer/methods-auto/recordref/recordref-insert--method).
            // Cover all three call shapes so a regression that only handles one of them is caught.
            File.WriteAllText(Path.Combine(testDir, "Test.al"), """
                codeunit 50200 "AutoInc Stub Tests"
                {
                    Subtype = Test;

                    var Assert: Codeunit Assert;

                    [Test]
                    procedure InsertTrue_TwoInserts_GetDistinctAutoIncIds()
                    var
                        R1: Record "Auto Inc Lib Table";
                        R2: Record "Auto Inc Lib Table";
                    begin
                        R1.Init();
                        R1.Name := 'A';
                        R1.Insert(true);

                        R2.Init();
                        R2.Name := 'B';
                        R2.Insert(true);

                        Assert.AreNotEqual(0, R1."Entry No.", 'First Insert(true) should auto-assign a non-zero id');
                        Assert.AreNotEqual(0, R2."Entry No.", 'Second Insert(true) should auto-assign a non-zero id');
                        Assert.AreNotEqual(R1."Entry No.", R2."Entry No.", 'Insert(true) AutoIncrement IDs should be distinct');
                    end;

                    [Test]
                    procedure InsertFalse_TwoInserts_GetDistinctAutoIncIds()
                    var
                        R1: Record "Auto Inc Lib Table";
                        R2: Record "Auto Inc Lib Table";
                    begin
                        R1.Init();
                        R1.Name := 'A';
                        R1.Insert(false);

                        R2.Init();
                        R2.Name := 'B';
                        R2.Insert(false);

                        Assert.AreNotEqual(0, R1."Entry No.", 'First Insert(false) should auto-assign a non-zero id');
                        Assert.AreNotEqual(0, R2."Entry No.", 'Second Insert(false) should auto-assign a non-zero id');
                        Assert.AreNotEqual(R1."Entry No.", R2."Entry No.", 'Insert(false) AutoIncrement IDs should be distinct');
                    end;

                    [Test]
                    procedure InsertNoArg_TwoInserts_GetDistinctAutoIncIds()
                    var
                        R1: Record "Auto Inc Lib Table";
                        R2: Record "Auto Inc Lib Table";
                    begin
                        R1.Init();
                        R1.Name := 'A';
                        R1.Insert();

                        R2.Init();
                        R2.Name := 'B';
                        R2.Insert();

                        Assert.AreNotEqual(0, R1."Entry No.", 'First Insert() should auto-assign a non-zero id');
                        Assert.AreNotEqual(0, R2."Entry No.", 'Second Insert() should auto-assign a non-zero id');
                        Assert.AreNotEqual(R1."Entry No.", R2."Entry No.", 'Insert() AutoIncrement IDs should be distinct');
                    end;
                }
                """);

            var result = RunPipeline(testDir, pkgDir);

            AssertPipelinePassed(result);
            Assert.Equal(3, result.Passed);
        });
    }

    [Fact]
    public async Task AutoStub_PreservesPkUniqueness_DuplicateInsertErrors()
    {
        if (AlcPath == null) return;

        await RunAutoStubScenario(async (testDir, pkgDir) =>
        {
            var libGuid = Guid.NewGuid();
            var testGuid = Guid.NewGuid();
            await BuildLibraryApp(libGuid, pkgDir);
            WriteTestAppJson(testDir, testGuid, libGuid);

            File.WriteAllText(Path.Combine(testDir, "Test.al"), """
                codeunit 50201 "AutoInc Stub Dup Tests"
                {
                    Subtype = Test;

                    var Assert: Codeunit Assert;

                    [Test]
                    procedure InsertExplicitDuplicate_Errors()
                    var
                        R1: Record "Auto Inc Lib Table";
                        R2: Record "Auto Inc Lib Table";
                    begin
                        R1.Init();
                        R1."Entry No." := 5;
                        R1.Name := 'A';
                        R1.Insert();
                        // Explicit value must survive Insert; catches a zombie that
                        // overwrites every Integer PK with a generated id.
                        Assert.AreEqual(5, R1."Entry No.", 'Explicit PK value should be preserved');

                        R2.Init();
                        R2."Entry No." := 5;
                        R2.Name := 'B';
                        asserterror R2.Insert();
                        Assert.ExpectedError('already exists');
                    end;
                }
                """);

            var result = RunPipeline(testDir, pkgDir);

            AssertPipelinePassed(result);
            Assert.Equal(1, result.Passed);
        });
    }

    private static void AssertPipelinePassed(PipelineResult result)
    {
        if (result.ExitCode == 0 && result.Failed == 0 && result.Errors == 0)
            return;

        var failures = string.Join("\n",
            result.Tests.Where(t => t.Status != TestStatus.Pass)
                .Select(t => $"  {t.Status} {t.Name}: {t.Message}"));
        Assert.Fail(
            $"Pipeline failed (exit={result.ExitCode}, passed={result.Passed}, failed={result.Failed}, errors={result.Errors}).\n" +
            $"Failures:\n{failures}\n" +
            $"--- StdOut ---\n{result.StdOut}\n" +
            $"--- StdErr ---\n{result.StdErr}");
    }

    [Fact]
    public async Task AutoStub_PreservesCompositePrimaryKey_DistinctTuplesCoexist()
    {
        if (AlcPath == null) return;

        await RunAutoStubScenario(async (testDir, pkgDir) =>
        {
            var libGuid = Guid.NewGuid();
            var testGuid = Guid.NewGuid();
            await BuildCompositeKeyLibraryApp(libGuid, pkgDir);
            WriteTestAppJsonForCompositeLib(testDir, testGuid, libGuid);

            File.WriteAllText(Path.Combine(testDir, "Test.al"), """
                codeunit 50202 "Composite PK Stub Tests"
                {
                    Subtype = Test;

                    var Assert: Codeunit Assert;

                    [Test]
                    procedure DistinctCompositeKeys_Coexist_DuplicateErrors()
                    var
                        R1: Record "Composite PK Lib Table";
                        R2: Record "Composite PK Lib Table";
                        R3: Record "Composite PK Lib Table";
                    begin
                        R1."Item No." := 'A';
                        R1."Line No." := 10;
                        R1.Value := 'first';
                        R1.Insert();

                        R2."Item No." := 'A';
                        R2."Line No." := 20;
                        R2.Value := 'second';
                        R2.Insert();

                        R3."Item No." := 'B';
                        R3."Line No." := 10;
                        R3.Value := 'third';
                        R3.Insert();

                        Assert.AreEqual(3, R1.Count(), 'Composite-PK rows with distinct tuples should coexist');

                        R1.Reset();
                        R1."Item No." := 'A';
                        R1."Line No." := 10;
                        R1.Value := 'collision';
                        asserterror R1.Insert();
                        Assert.ExpectedError('already exists');
                    end;
                }
                """);

            var result = RunPipeline(testDir, pkgDir);

            AssertPipelinePassed(result);
            Assert.Equal(1, result.Passed);
        });
    }

    private async Task BuildCompositeKeyLibraryApp(Guid libGuid, string pkgDir)
    {
        var libDir = Path.Combine(Path.GetDirectoryName(pkgDir)!, "lib-composite");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(libDir, "Table.al"), """
            table 50101 "Composite PK Lib Table"
            {
                fields
                {
                    field(1; "Item No."; Code[20]) { }
                    field(2; "Line No."; Integer) { }
                    field(3; Value; Text[50]) { }
                }
                keys
                {
                    key(PK; "Item No.", "Line No.") { Clustered = true; }
                }
            }
            """);

        File.WriteAllText(Path.Combine(libDir, "app.json"), $$"""
            {
              "id": "{{libGuid}}",
              "name": "CompositePkLib",
              "publisher": "AlRunnerTest",
              "version": "1.0.0.0",
              "runtime": "14.0"
            }
            """);

        var libAppPath = await CompileAlApp(libDir);
        if (libAppPath == null)
            throw new InvalidOperationException("Failed to compile composite-PK library .app via alc.");

        File.Copy(libAppPath, Path.Combine(pkgDir, Path.GetFileName(libAppPath)));
    }

    private static void WriteTestAppJsonForCompositeLib(string testDir, Guid testGuid, Guid libGuid)
    {
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
            {
              "id": "{{testGuid}}",
              "name": "CompositePkTest",
              "publisher": "AlRunnerTest",
              "version": "1.0.0.0",
              "runtime": "14.0",
              "dependencies": [
                {
                  "id": "{{libGuid}}",
                  "name": "CompositePkLib",
                  "publisher": "AlRunnerTest",
                  "version": "1.0.0.0"
                }
              ]
            }
            """);
    }

    private async Task RunAutoStubScenario(Func<string, string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-autoincstub-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var pkgDir = Path.Combine(dir, "pkg");
            var testDir = Path.Combine(dir, "test");
            Directory.CreateDirectory(pkgDir);
            Directory.CreateDirectory(testDir);

            await body(testDir, pkgDir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private async Task BuildLibraryApp(Guid libGuid, string pkgDir)
    {
        var libDir = Path.Combine(Path.GetDirectoryName(pkgDir)!, "lib");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(libDir, "Table.al"), """
            table 50100 "Auto Inc Lib Table"
            {
                fields
                {
                    field(1; "Entry No."; Integer) { AutoIncrement = true; }
                    field(2; Name; Text[50]) { }
                }
                keys
                {
                    key(PK; "Entry No.") { Clustered = true; }
                }
            }
            """);

        File.WriteAllText(Path.Combine(libDir, "app.json"), $$"""
            {
              "id": "{{libGuid}}",
              "name": "AutoIncLib",
              "publisher": "AlRunnerTest",
              "version": "1.0.0.0",
              "runtime": "14.0"
            }
            """);

        var libAppPath = await CompileAlApp(libDir);
        if (libAppPath == null)
            throw new InvalidOperationException("Failed to compile library .app via alc — alc.exe found but compilation failed.");

        File.Copy(libAppPath, Path.Combine(pkgDir, Path.GetFileName(libAppPath)));
    }

    private static void WriteTestAppJson(string testDir, Guid testGuid, Guid libGuid)
    {
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
            {
              "id": "{{testGuid}}",
              "name": "AutoIncTest",
              "publisher": "AlRunnerTest",
              "version": "1.0.0.0",
              "runtime": "14.0",
              "dependencies": [
                {
                  "id": "{{libGuid}}",
                  "name": "AutoIncLib",
                  "publisher": "AlRunnerTest",
                  "version": "1.0.0.0"
                }
              ]
            }
            """);
    }

    private static PipelineResult RunPipeline(string testDir, string pkgDir)
    {
        var pipeline = new AlRunnerPipeline();
        return pipeline.Run(new PipelineOptions
        {
            TestIsolation = TestIsolation.Method,
            InputPaths = { testDir },
            PackagePaths = { pkgDir },
            Strict = true
        });
    }

    private static async Task<string?> CompileAlApp(string projectDir)
    {
        if (AlcPath == null) return null;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = AlcPath,
            Arguments = $"/project:\"{projectDir}\" /outfolder:\"{projectDir}\" /packagecachepath:\"{projectDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return null;
        return Directory.GetFiles(projectDir, "*.app").FirstOrDefault();
    }
}
