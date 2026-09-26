// TestDataTenantWideTablesTests — #4770: --test-data hydrates tables that are not per company
// (the catalog lists them under company "-"), reads them without `--company`, and counts the
// tenant-wide platform tables it deliberately leaves to the runner.
//
// Runner-side by construction: which tables a backup read covers, and which reader arguments
// it passes, are properties of the runner. The end-to-end half (AL reads Tenant Media rows a
// hydrated Media id names) is tests/test-data-fixture/TestDataTenantTables.Codeunit.al, which
// needs a W1 backup no CI leg has.
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// SetResolvedDeps writes BcCompiler's process-wide statics (see TestDataLazyLoadPolicyTests).
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class TestDataTenantWideTablesTests : IDisposable
{
    private const string Cronus = "CRONUS";
    private const int CompanyTableId = 61030;
    private const int TenantAppTableId = 61031;
    private const int TenantMediaId = 2000000184;
    private const int CompanySystemTableId = 2000000006;

    private readonly DirectoryInfo _dir;
    private readonly string _log;
    private readonly string? _previousEnv;

    public TestDataTenantWideTablesTests()
    {
        _dir = Directory.CreateDirectory(TestScratch.Dir("al-runner-tenant-testdata"));
        _log = Path.Combine(_dir.FullName, "reader-invocations.log");
        var backup = Path.Combine(_dir.FullName, "BusinessCentral-W1.bak");
        File.WriteAllBytes(backup, new byte[256]);

        var reader = Path.Combine(_dir.FullName, "bcbak");
        File.WriteAllText(reader, FakeReaderScript(_log));
        File.SetUnixFileMode(reader,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _previousEnv = Environment.GetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar);
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, reader);
        BackupReaderTool.ResetForTests();
        TestDataOptions.ResetForTests();
        TestDataProvisioner.ResetForTests();

        var app = Path.Combine(_dir.FullName, "Fake_App_1_0_0_0.app");
        File.WriteAllBytes(app, new byte[8]);
        BcCompiler.SetResolvedDeps(
            new[]
            {
                (new AppManifest("Fake", "App", new Version(1, 0, 0, 0), Guid.NewGuid(),
                    Array.Empty<DependencyRef>()), app),
            },
            new[] { _dir.FullName });

        TestDataOptions.Enabled = true;
        TestDataOptions.ExplicitBackupPath = backup;
        TestDataOptions.CompanyOverride = Cronus;
    }

    public void Dispose()
    {
        TestDataProvisioner.ResetForTests();
        TestDataOptions.ResetForTests();
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, _previousEnv);
        BackupReaderTool.ResetForTests();
        BcCompiler.SetResolvedDeps(Array.Empty<(AppManifest, string)>(), Array.Empty<string>());
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>A `bcbak` stand-in logging `cmd|table|company` per call (company `none` when
    /// no `--company` was passed). `read` answers an empty array, so no booted engine is needed:
    /// what is asserted is which tables are read and with which arguments.</summary>
    private static string FakeReaderScript(string logPath) =>
        "#!/bin/sh\n"
        + $"log='{logPath}'\n"
        + "cmd=\"$1\"\n"
        + "table=''\n"
        + "company='none'\n"
        + "while [ $# -gt 0 ]; do\n"
        + "  case \"$1\" in\n"
        + "    --table) table=\"$2\" ;;\n"
        + "    --company) company=\"$2\" ;;\n"
        + "  esac\n"
        + "  shift\n"
        + "done\n"
        + "echo \"$cmd|$table|$company\" >> \"$log\"\n"
        + "case \"$cmd\" in\n"
        + $"  companies) echo '{Cronus}' ;;\n"
        + $"  tables) printf '%s\\n' '   7 page\t{Cronus}\tCompanyTable\t{CompanyTableId} \"CompanyTable\" (Fake App)' "
            + $"'  69 page\t-\tTenant Media\t{TenantMediaId} \"Tenant Media\" (System)' "
            + $"' 491 page\t-\tTenantAppTable\t{TenantAppTableId} \"TenantAppTable\" (Fake App)' "
            + $"'   2 page\t-\tCompany\t{CompanySystemTableId} \"Company\" (System)' "
            + "' 137 none\t-\t$ndo$textmap\t-' ;;\n"
        + "  read) echo '[]' ;;\n"
        + "esac\n";

    private string[] TableReads()
        => File.Exists(_log)
            ? File.ReadAllLines(_log).Where(l => l.StartsWith("read|", StringComparison.Ordinal)).ToArray()
            : Array.Empty<string>();

    [Fact]
    public void BuildPlan_TakesTenantWideTables_AndHoldsBackTenantWideSystemTables()
    {
        var entries = new List<BackupTableEntry>
        {
            new(7, "page", Cronus, "CompanyTable", CompanyTableId, "Fake App"),
            new(69, "page", "-", "Tenant Media", TenantMediaId, "System"),
            new(33, "page", "-", "Tenant Media Set", 2000000183, "System"),
            new(491, "page", "-", "TenantAppTable", TenantAppTableId, "Fake App"),
            new(2, "page", "-", "Company", CompanySystemTableId, "System"),
            new(0, "none", "-", "User", 2000000120, "System"),        // no rows: nothing withheld
            new(137, "none", "-", "$ndo$textmap", null, null),         // no AL table
            new(4, "page", "My Company", "OtherCompanyTable", 61032, "Fake App"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, Cronus, Array.Empty<AppManifest>());

        Assert.Equal(
            new[] { "CompanyTable", "Tenant Media", "Tenant Media Set", "TenantAppTable" },
            plan.Hydratable.Select(e => e.TableName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { CompanySystemTableId }, plan.TenantSystemRefusals.Keys.ToArray());
        Assert.Contains("'Company'", plan.TenantSystemRefusals[CompanySystemTableId], StringComparison.Ordinal);
        Assert.Contains("tenant-wide platform table", plan.TenantSystemRefusals[CompanySystemTableId], StringComparison.Ordinal);
        Assert.Equal(0, plan.SkippedAmbiguous);
    }

    [Fact]
    public void BuildPlan_MergeProbePrefersACompanyTable()
    {
        // A tenant-wide companion with rows sorts first by name; the probe must still read the
        // company table, which every backup has.
        var entries = new List<BackupTableEntry>
        {
            new(1, "page", "-", "Application Area Setup", 9178, "Base Application"),
            new(1, "page", "-", "Application Area Setup$ext", 9178, "Base Application"),
            new(1, "page", Cronus, "Source Code Setup", 242, "Business Foundation"),
            new(1, "page", Cronus, "Source Code Setup$ext", null, null),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, Cronus, Array.Empty<AppManifest>());

        Assert.Equal(new[] { "Application Area Setup", "Source Code Setup" },
            plan.ExtendedTableNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("Source Code Setup", plan.MergeProbe!.TableName);
    }

    [Fact]
    public void ATenantWideTable_IsReadWithoutACompany_AndACompanyTableWithOne()
    {
        TestDataProvisioner.Arm();
        Assert.Contains(TenantMediaId, TestDataProvisioner.ArmedTableIds);
        Assert.Contains(TenantAppTableId, TestDataProvisioner.ArmedTableIds);

        var loader = RecordPatches.TestDataOnDemandLoader!;
        loader(new object(), TenantMediaId);
        loader(new object(), TenantAppTableId);
        loader(new object(), CompanyTableId);

        var reads = TableReads();
        Assert.Contains("read|Tenant Media|none", reads);
        Assert.Contains("read|TenantAppTable|none", reads);
        Assert.Contains($"read|CompanyTable|{Cronus}", reads);
        Assert.Equal(3, reads.Length);
        Assert.Contains("(tenant-wide)", TestDataProvisioner.TableOutcome(TenantMediaId)!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATenantWideSystemTable_IsNotRead_AndIsCountedAndExplained()
    {
        TestDataProvisioner.Arm();
        Assert.DoesNotContain(CompanySystemTableId, TestDataProvisioner.ArmedTableIds);

        // Counted from arm time, before any table loads.
        var armed = TestDataProvisioner.LastSummary;
        Assert.NotNull(armed);
        Assert.Equal(1, armed!.TenantSystemTablesNotLoaded);
        Assert.Contains("1 tenant-wide system table(s) not loaded", armed.Describe(verbose: false), StringComparison.Ordinal);

        RecordPatches.TestDataOnDemandLoader!(new object(), CompanySystemTableId);

        Assert.Empty(TableReads());
        var outcome = TestDataProvisioner.TableOutcome(CompanySystemTableId);
        Assert.NotNull(outcome);
        Assert.Contains("not loaded", outcome!, StringComparison.Ordinal);
        Assert.Contains("tenant-wide platform table", outcome, StringComparison.Ordinal);

        // …and still counted after an ordinary load updates the summary.
        RecordPatches.TestDataOnDemandLoader!(new object(), CompanyTableId);
        Assert.Equal(1, TestDataProvisioner.LastSummary!.TenantSystemTablesNotLoaded);
    }
}
