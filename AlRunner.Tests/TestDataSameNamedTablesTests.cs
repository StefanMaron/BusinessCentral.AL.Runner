// TestDataSameNamedTablesTests — the proving tests for issue #2264: two installed apps may each
// declare a table of the same AL name in one company (Base Application's and Power BI Report
// embeddings' "Dimension Set Entry"), and --test-data now reads each one by the app that owns
// the AL table id the runner resolved, instead of refusing both.
//
// Runner policy, not BC behaviour: which `--app` the runner hands its backup reader. The row-level
// half (AL reads 480's rows back) needs a real backup and lives in tests/test-data-fixture/.
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataSameNamedTablesPlanTests
{
    private const string Company = "CRONUS International Ltd_";
    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");
    private static readonly Guid PowerBiId = Guid.Parse("6d72c93d-164a-494c-8d65-24d7f41d7b61");

    private static AppManifest App(string name, Guid id)
        => new("Microsoft", name, new Version(28, 1, 0, 0), id, Array.Empty<DependencyRef>());

    private static readonly AppManifest[] Closure =
    {
        App("Base Application", BaseAppId),
        App("Power BI Report embeddings", PowerBiId),
    };

    [Fact]
    public void SameNamedTablesFromTwoClosureApps_AreBothPlanned_EachWithItsOwnApp()
    {
        var entries = new List<BackupTableEntry>
        {
            new(89, "page", Company, "Dimension Set Entry", 480, "Base Application"),
            new(5, "page", Company, "Dimension Set Entry", 36950, "Power BI Report embeddings"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, Company, Closure);

        Assert.Equal(0, plan.SkippedAmbiguous);
        Assert.Empty(plan.AmbiguousRefusals);
        Assert.Equal(2, plan.Hydratable.Count);
        // The id, per table — not merely that an --app was attached to something.
        Assert.Equal(BaseAppId.ToString("D"), plan.Hydratable.Single(e => e.AlTableId == 480).ReadAppId);
        Assert.Equal(PowerBiId.ToString("D"), plan.Hydratable.Single(e => e.AlTableId == 36950).ReadAppId);
    }

    [Fact]
    public void AnUnambiguousTable_IsReadWithoutAnAppSelector()
    {
        var entries = new List<BackupTableEntry>
        {
            new(119, "page", Company, "No_ Series", 308, "Base Application"),
            // Same name in ANOTHER company is not ambiguity: --company already separates it.
            new(7, "page", "My Company", "No_ Series", 308, "Base Application"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, Company, Closure);

        Assert.Null(Assert.Single(plan.Hydratable).ReadAppId);
    }

    [Fact]
    public void OwningAppNameMatchingNoClosureApp_StaysRefused_AndSaysWhy()
    {
        var entries = new List<BackupTableEntry>
        {
            new(89, "page", Company, "Dimension Set Entry", 480, "Base Application"),
            new(5, "page", Company, "Dimension Set Entry", 36950, "Some Renamed App"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, Company, Closure);

        // The resolvable candidate is still planned; only the one with no app id is refused.
        Assert.Equal(480, Assert.Single(plan.Hydratable).AlTableId);
        Assert.Equal(1, plan.SkippedAmbiguous);
        Assert.Contains("Some Renamed App", plan.AmbiguousRefusals[36950], StringComparison.Ordinal);
    }

    [Fact]
    public void TwoClosureAppsSharingTheOwningAppName_StayRefused()
    {
        var entries = new List<BackupTableEntry>
        {
            new(89, "page", Company, "Dimension Set Entry", 480, "Base Application"),
            new(5, "page", Company, "Dimension Set Entry", 36950, "Power BI Report embeddings"),
        };
        var closure = Closure.Append(App("Power BI Report embeddings", Guid.NewGuid())).ToArray();

        var plan = TestDataProvisioner.BuildPlan(entries, Company, closure);

        Assert.Equal(480, Assert.Single(plan.Hydratable).AlTableId);
        Assert.Equal(1, plan.SkippedAmbiguous);
        Assert.Contains("2 apps", plan.AmbiguousRefusals[36950], StringComparison.Ordinal);
    }

    [Fact]
    public void TwoCandidatesOfOneApp_StayRefused_BecauseAnAppSelectorCannotSeparateThem()
    {
        var entries = new List<BackupTableEntry>
        {
            new(89, "page", Company, "Dimension Set Entry", 480, "Base Application"),
            new(5, "page", Company, "Dimension Set Entry", 481, "Base Application"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, Company, Closure);

        Assert.Empty(plan.Hydratable);
        Assert.Equal(2, plan.SkippedAmbiguous);
        Assert.Contains("Base Application", plan.AmbiguousRefusals[480], StringComparison.Ordinal);
        Assert.Contains("Base Application", plan.AmbiguousRefusals[481], StringComparison.Ordinal);
    }
}

// SetResolvedDeps writes BcCompiler's process-wide statics (see BcCompilerSharedReferenceCollection).
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class TestDataSameNamedTablesLoadTests : IDisposable
{
    private const int AppOneTableId = 61013;
    private const int AppTwoTableId = 61014;
    private const int UnownedTableId = 61015;
    private static readonly Guid AppOneId = Guid.Parse("11111111-aaaa-4aaa-8aaa-111111111111");
    private static readonly Guid AppTwoId = Guid.Parse("22222222-bbbb-4bbb-8bbb-222222222222");

    private readonly DirectoryInfo _dir;
    private readonly string _log;
    private readonly string? _previousEnv;

    public TestDataSameNamedTablesLoadTests()
    {
        _dir = Directory.CreateDirectory(TestScratch.Dir("al-runner-same-named-testdata"));
        _log = Path.Combine(_dir.FullName, "reader-invocations.log");
        var backup = Path.Combine(_dir.FullName, "BusinessCentral-W1.bak");
        File.WriteAllBytes(backup, new byte[256]);

        var reader = Path.Combine(_dir.FullName, "bcbak");
        File.WriteAllText(reader, FakeReaderScript(_log));
        File.SetUnixFileMode(reader, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _previousEnv = Environment.GetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar);
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, reader);
        BackupReaderTool.ResetForTests();
        TestDataOptions.ResetForTests();
        TestDataProvisioner.ResetForTests();

        var appOne = Path.Combine(_dir.FullName, "Fake_App One_1_0_0_0.app");
        var appTwo = Path.Combine(_dir.FullName, "Fake_App Two_1_0_0_0.app");
        File.WriteAllBytes(appOne, new byte[8]);
        File.WriteAllBytes(appTwo, new byte[8]);
        BcCompiler.SetResolvedDeps(
            new[]
            {
                (new AppManifest("Fake", "App One", new Version(1, 0, 0, 0), AppOneId, Array.Empty<DependencyRef>()), appOne),
                (new AppManifest("Fake", "App Two", new Version(1, 0, 0, 0), AppTwoId, Array.Empty<DependencyRef>()), appTwo),
            },
            new[] { _dir.FullName });

        TestDataOptions.Enabled = true;
        TestDataOptions.ExplicitBackupPath = backup;
        TestDataOptions.CompanyOverride = "CRONUS";
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

    /// <summary>
    /// A `bcbak` stand-in that behaves like the real reader on a shared name: a `read` of
    /// `Dim Set` without `--app` fails with the reader's own "ambiguous table" error, and with an
    /// app prefix it answers. It logs `cmd|table|app|top|merge`. The third `Dim Set`, owned by an
    /// app outside the closure, is what keeps "refuse when the closure cannot say" reachable.
    /// App Two's `$ext` companion carries rows, so the merge probe lands on a shared name too.
    /// </summary>
    private static string FakeReaderScript(string logPath) =>
        "#!/bin/sh\n"
        + $"log='{logPath}'\n"
        + "cmd=\"$1\"\n"
        + "table=''\n"
        + "app=''\n"
        + "top=''\n"
        + "merge='plain'\n"
        + "while [ $# -gt 0 ]; do\n"
        + "  case \"$1\" in\n"
        + "    --table) table=\"$2\" ;;\n"
        + "    --app) app=\"$2\" ;;\n"
        + "    --top) top=\"top$2\" ;;\n"
        + "    --merge-extensions) merge='merged' ;;\n"
        + "  esac\n"
        + "  shift\n"
        + "done\n"
        + "echo \"$cmd|$table|$app|$top|$merge\" >> \"$log\"\n"
        + "case \"$cmd\" in\n"
        + "  companies) echo 'CRONUS' ;;\n"
        + "  tables) printf '%s\\n' "
            + $"'  89 page\tCRONUS\tDim Set\t{AppOneTableId} \"Dim Set\" (App One)' "
            + $"'   5 page\tCRONUS\tDim Set\t{AppTwoTableId} \"Dim Set\" (App Two)' "
            + $"'   3 page\tCRONUS\tDim Set\t{UnownedTableId} \"Dim Set\" (App Three)' "
            + $"'   2 page\tCRONUS\tDim Set$ext\t{AppTwoTableId} \"Dim Set\" (App Two)' ;;\n"
        + "  read)\n"
        + "    if [ \"$table\" = 'Dim Set' ] && [ -z \"$app\" ]; then\n"
        + "      echo \"error: ambiguous table 'Dim Set' — use --app <app-id-prefix> to select the defining app\" >&2\n"
        + "      exit 1\n"
        + "    fi\n"
        + "    if [ -n \"$top\" ]; then\n"
        + "      if [ \"$merge\" = 'merged' ]; then echo '[{\"A\":1,\"B\":2}]'; else echo '[{\"A\":1}]'; fi\n"
        + "    else\n"
        + "      echo '[]'\n"
        + "    fi ;;\n"
        + "esac\n";

    private string[] Reads()
        => File.Exists(_log)
            ? File.ReadAllLines(_log).Where(l => l.StartsWith("read|", StringComparison.Ordinal)).ToArray()
            : Array.Empty<string>();

    [Fact]
    public void EachSameNamedTable_IsReadWithTheAppThatOwnsItsAlTableId()
    {
        TestDataProvisioner.Arm();

        // The probe hit a shared name, so it had to carry App Two's id or Arm would have thrown.
        Assert.All(Reads(), r => Assert.Equal($"read|Dim Set|{AppTwoId:D}", string.Join('|', r.Split('|')[..3])));

        RecordPatches.TestDataOnDemandLoader!(new object(), AppOneTableId);
        RecordPatches.TestDataOnDemandLoader!(new object(), AppTwoTableId);

        var loads = Reads().Where(r => !r.Contains("|top1|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[]
        {
            $"read|Dim Set|{AppOneId:D}||merged",
            $"read|Dim Set|{AppTwoId:D}||merged",
        }, loads);

        var summary = TestDataProvisioner.LastSummary;
        Assert.NotNull(summary);
        Assert.Equal(0, summary!.TablesRefusedByReader);
        Assert.Equal(1, summary.TablesSkippedAmbiguous);
    }

    [Fact]
    public void ASameNamedTableTheClosureCannotAttribute_RunsNoReader_AndItsOutcomeSaysWhy()
    {
        TestDataProvisioner.Arm();
        var before = Reads().Length;

        RecordPatches.TestDataOnDemandLoader!(new object(), UnownedTableId);

        Assert.Equal(before, Reads().Length);
        var outcome = TestDataProvisioner.TableOutcome(UnownedTableId);
        Assert.NotNull(outcome);
        Assert.Contains("App Three", outcome!, StringComparison.Ordinal);
        Assert.DoesNotContain("has no table with this id", outcome, StringComparison.Ordinal);
    }
}
