// TestDataLazyHydrationTests — the proving tests for issue #2262: --test-data loads a table
// on FIRST TOUCH instead of loading the whole company before the install triggers.
//
// WHAT IS PROVED HERE, AND WHY IT IS HERE
//   Both claims below are about the RUNNER — when a backup table is read, and where its rows
//   are recorded so they survive a boundary. Neither is a statement about what Business
//   Central does with AL source, so .claude/rules/bc-behavior-tests-go-upstream.md does not
//   send them to the corpus. The end-to-end half (AL reads the hydrated rows back, a modified
//   row comes back at the next boundary, an Insert onto a backup key raises a duplicate-key
//   error) lives in tests/test-data-fixture/, which needs a ~1 GB backup no CI leg has.
//
// THE CENTRAL CLAIM IS A NEGATIVE, AND IT IS NOT OBSERVABLE FROM AL
//   "A table nothing touches is never loaded" cannot be asserted in AL, by construction: the
//   whole correctness property of this design is that AL cannot tell a table materialised on
//   first touch from one present from the start. So it is asserted here instead, at the level
//   the saving actually happens — the reader invocations. A fake `bcbak` on the
//   AL_RUNNER_BCBAK seam records every command it is given, so "no table was read until one
//   was touched" and "touching table A read A and nothing else" are direct assertions on that
//   log rather than an inference from a timing number.
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// SetResolvedDeps writes BcCompiler's process-wide statics, which this collection exists to
// serialise (see BcCompilerSharedReferenceCollection).
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class TestDataLazyLoadPolicyTests : IDisposable
{
    private readonly DirectoryInfo _dir;
    private readonly string _log;
    private readonly string _backup;
    private readonly string? _previousEnv;

    // AL table ids the fake catalog offers. 61010/61011 are arbitrary and never resolved
    // against real metadata — every read below returns zero rows, so the hydration mechanism
    // returns before it needs a booted engine. What is under test is WHICH tables get read.
    private const int TouchedTableId = 61010;
    private const int UntouchedTableId = 61011;
    private const int NotInTheBackupTableId = 61012;

    public TestDataLazyLoadPolicyTests()
    {
        _dir = Directory.CreateTempSubdirectory("al-runner-lazy-testdata");
        _log = Path.Combine(_dir.FullName, "reader-invocations.log");
        _backup = Path.Combine(_dir.FullName, "BusinessCentral-W1.bak");
        File.WriteAllBytes(_backup, new byte[256]);

        var reader = Path.Combine(_dir.FullName, "bcbak");
        File.WriteAllText(reader, FakeReaderScript(_log));
        File.SetUnixFileMode(reader,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _previousEnv = Environment.GetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar);
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, reader);
        BackupReaderTool.ResetForTests();
        TestDataOptions.ResetForTests();
        TestDataProvisioner.ResetForTests();

        // A dependency closure whose .app paths exist — the symbol set the reader is told the
        // schema comes from. Content is irrelevant here; the fake reader never opens them.
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
        TestDataOptions.ExplicitBackupPath = _backup;
        TestDataOptions.CompanyOverride = "CRONUS";
    }

    public void Dispose()
    {
        TestDataProvisioner.ResetForTests();
        TestDataOptions.ResetForTests();
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, _previousEnv);
        BackupReaderTool.ResetForTests();
        BcCompiler.SetResolvedDeps(
            Array.Empty<(AppManifest, string)>(), Array.Empty<string>());
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A `bcbak` stand-in. Appends its command word (plus the --table it was given) to a log
    /// and answers the three commands the provisioner issues. `read` always returns an empty
    /// row array: what these tests assert is WHICH tables are read, and a zero-row answer
    /// keeps the mechanism from needing a booted BC engine to build records with.
    ///
    /// The `$ext` companion in the catalog is deliberate — it is what puts a table in the
    /// plan's ExtendedTableNames and therefore makes AssertMergeIsHonoured run, so the
    /// once-per-run test below is asserting against a probe that really fired.
    /// </summary>
    private static string FakeReaderScript(string logPath) =>
        "#!/bin/sh\n"
        + $"log='{logPath}'\n"
        + "cmd=\"$1\"\n"
        + "table=''\n"
        + "top=''\n"
        + "merge='plain'\n"
        + "while [ $# -gt 0 ]; do\n"
        + "  case \"$1\" in\n"
        + "    --table) table=\"$2\" ;;\n"
        + "    --top) top=\"top$2\" ;;\n"
        + "    --merge-extensions) merge='merged' ;;\n"
        + "  esac\n"
        + "  shift\n"
        + "done\n"
        + "echo \"$cmd|$table|$top|$merge\" >> \"$log\"\n"
        + "case \"$cmd\" in\n"
        + "  companies) echo 'CRONUS' ;;\n"
        + $"  tables) printf '%s\\n' '   7 Table\tCRONUS\tTouched\t{TouchedTableId} \"Touched\" (Fake App)' "
            + $"'   9 Table\tCRONUS\tUntouched\t{UntouchedTableId} \"Untouched\" (Fake App)' "
            + "'   3 Table\tCRONUS\tTouched$ext\t-' ;;\n"
        + "  read)\n"
        + "    if [ -n \"$top\" ]; then\n"
        // The merge probe: the merged read must return strictly more columns than the plain
        // one, which is exactly what AssertMergeIsHonoured requires.
        + "      if [ \"$merge\" = 'merged' ]; then echo '[{\"A\":1,\"B\":2}]'; else echo '[{\"A\":1}]'; fi\n"
        + "    else\n"
        + "      echo '[]'\n"
        + "    fi ;;\n"
        + "esac\n";

    private string[] ReaderInvocations()
        => File.Exists(_log)
            ? File.ReadAllLines(_log).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();

    /// <summary>Reads of a real table — the merge probe's two --top 1 reads excluded, since
    /// those are a property check on the reader and not a table load.</summary>
    private string[] TableLoadReads()
        => ReaderInvocations()
            .Where(l => l.StartsWith("read|", StringComparison.Ordinal) && !l.Contains("|top1|", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// THE claim of #2262: arming reads no table rows. Under the eager policy this same call
    /// read every one of the 315 in-scope CRONUS tables (37,710 rows) before a single test
    /// body ran, and RestoreInstallBaselineSnapshot then re-inserted all of them at every
    /// codeunit and every test boundary.
    /// </summary>
    [Fact]
    public void Arm_ReadsTheCatalogButNoTableRows()
    {
        TestDataProvisioner.Arm();

        Assert.Contains(ReaderInvocations(), l => l.StartsWith("companies|", StringComparison.Ordinal));
        Assert.Contains(ReaderInvocations(), l => l.StartsWith("tables|", StringComparison.Ordinal));
        Assert.Empty(TableLoadReads());

        // Both tables really are in scope, so "nothing was read" is a statement about the
        // POLICY and not about a plan that never knew the tables existed.
        Assert.Contains(TouchedTableId, TestDataProvisioner.ArmedTableIds);
        Assert.Contains(UntouchedTableId, TestDataProvisioner.ArmedTableIds);

        // No table loaded means no hydration outcome to report yet.
        Assert.Null(TestDataProvisioner.LastSummary);
    }

    /// <summary>
    /// The saving, stated as the negative it is: touching one table loads THAT table and
    /// leaves every other in-scope table unread. An implementation that kept the eager loop
    /// and merely moved it would fail on the second assertion.
    /// </summary>
    [Fact]
    public void TouchingOneTable_LoadsOnlyThatTable()
    {
        TestDataProvisioner.Arm();
        var loader = RecordPatches.TestDataOnDemandLoader;
        Assert.NotNull(loader);

        loader!(new object(), TouchedTableId);

        var reads = TableLoadReads();
        Assert.Single(reads);
        Assert.Contains("|Touched|", reads[0], StringComparison.Ordinal);
        // Merged, not plain: a load that dropped --merge-extensions would hydrate every
        // table-extension field blank and report success (#2261).
        Assert.EndsWith("|merged", reads[0], StringComparison.Ordinal);
        Assert.DoesNotContain(TableLoadReads(), l => l.Contains("|Untouched|", StringComparison.Ordinal));

        Assert.NotNull(TestDataProvisioner.LastSummary);
    }

    /// <summary>
    /// A table id the backup does not offer must cost nothing. This is the common case in a
    /// real run — the runner's own test tables, every virtual table, every table the company
    /// has no rows for — so a loader that invoked the reader for it would spend a subprocess
    /// per unknown table.
    /// </summary>
    [Fact]
    public void TouchingATableTheBackupDoesNotHave_RunsNoReader()
    {
        TestDataProvisioner.Arm();
        RecordPatches.TestDataOnDemandLoader!(new object(), NotInTheBackupTableId);

        Assert.Empty(TableLoadReads());
        Assert.Null(TestDataProvisioner.LastSummary);
    }

    /// <summary>
    /// AssertMergeIsHonoured stays a once-per-RUN reader probe. Whether the reader honours
    /// `--merge-extensions` is a property of the reader binary, not of a table, so re-asking
    /// per table would buy nothing and cost two extra subprocesses per loaded table. Arming
    /// again for the same symbol set must not re-probe either.
    /// </summary>
    [Fact]
    public void TheMergeProbe_RunsOncePerRun_NotOncePerTable()
    {
        TestDataProvisioner.Arm();
        var probeReads = ReaderInvocations().Count(l => l.Contains("|top1|", StringComparison.Ordinal));
        Assert.Equal(2, probeReads);   // one plain read, one merged read

        RecordPatches.TestDataOnDemandLoader!(new object(), TouchedTableId);
        RecordPatches.TestDataOnDemandLoader!(new object(), UntouchedTableId);
        TestDataProvisioner.Arm();

        Assert.Equal(2, ReaderInvocations().Count(l => l.Contains("|top1|", StringComparison.Ordinal)));
        Assert.Single(ReaderInvocations().Where(l => l.StartsWith("tables|", StringComparison.Ordinal)));
    }

    /// <summary>Without the flag nothing is armed and nothing is installed, so a default run
    /// pays exactly one null check per first-touch of a table and never opens a backup.</summary>
    [Fact]
    public void WithoutTheFlag_ArmInstallsNoLoaderAndTouchesNoReader()
    {
        TestDataOptions.ResetForTests();
        TestDataProvisioner.ResetForTests();

        TestDataProvisioner.Arm();

        Assert.Null(RecordPatches.TestDataOnDemandLoader);
        Assert.Empty(ReaderInvocations());
        Assert.Null(TestDataProvisioner.LastSummary);
    }
}

/// <summary>
/// The other half of #2262: a table loaded OUTSIDE the capture window has to be written into
/// the baselines the store is restored from, or the very next codeunit/test boundary wipes it
/// (RestoreInstallBaselineSnapshot begins with ResetPerTestState).
/// </summary>
public sealed class TestDataBaselineAppendTests : IDisposable
{
    private readonly List<RecordPatches.BaselineSource>? _savedInstallBaseline;

    public TestDataBaselineAppendTests()
    {
        // The tests below hand AppendBaselineTable a synthetic DataAccessSource. Leaving that
        // in the live per-app-group baseline would hand _mCreateTempDataAccess an object that
        // is not a DataAccessSource the next time anything restores.
        _savedInstallBaseline = RecordPatches.InstallBaselineForTests;
        RecordPatches.InstallBaselineForTests = null;
        RecordPatches.SetActiveDepCompanyBaseline(null);
    }

    public void Dispose()
    {
        RecordPatches.InstallBaselineForTests = _savedInstallBaseline;
        RecordPatches.SetActiveDepCompanyBaseline(null);
    }

    private static RecordPatches.InstallBaselineSnapshot EmptySnapshot()
        => new(new List<RecordPatches.BaselineSource>(), null, null, null);

    private static NavValue[][] Rows(params string[] values)
        => values.Select(v => new NavValue[] { new NavText(0, v) }).ToArray();

    /// <summary>
    /// Both targets, and that is deliberate. The per-app-group singleton is what a codeunit /
    /// test boundary restores; the dep+company snapshot is what the NEXT app group on the same
    /// dependency key is restored from before its own capture overwrites the singleton.
    /// Appending to only one would make a table's presence depend on which app group happened
    /// to touch it first.
    /// </summary>
    [Fact]
    public void AppendedTable_ReachesBothThePerGroupBaselineAndTheDepCompanySnapshot()
    {
        var source = new object();
        var meta = new object();
        var installBaseline = new List<RecordPatches.BaselineSource>
        {
            new(source, new List<RecordPatches.BaselineTable>()),
        };
        RecordPatches.InstallBaselineForTests = installBaseline;
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        RecordPatches.AppendBaselineTable(source, 61020, meta, Rows("S-ORD", "P-ORD"));

        var inGroup = Assert.Single(installBaseline[0].Tables);
        Assert.Equal(61020, inGroup.TableId);
        Assert.Equal(2, inGroup.Rows.Length);
        Assert.Equal("S-ORD", inGroup.Rows[0][0].ToString());

        // The dep+company snapshot had no BaselineSource for this source at all — one is
        // created, rather than the append being silently dropped.
        var depSource = Assert.Single(depCompany.Sources);
        Assert.Same(source, depSource.Source);
        var inDep = Assert.Single(depSource.Tables);
        Assert.Equal(61020, inDep.TableId);
        Assert.Equal("P-ORD", inDep.Rows[1][0].ToString());
    }

    /// <summary>
    /// The rows handed in are the PRISTINE load, and the triggering test runs against the live
    /// store immediately afterwards. If the baseline aliased those arrays, a test's first
    /// write would corrupt what every later boundary restores — the same discipline the
    /// capture and restore paths already keep with CloneValues.
    /// </summary>
    [Fact]
    public void AppendedRows_AreDeepCopied_SoALatetMutationCannotReachTheBaseline()
    {
        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        var pristine = Rows("ORIGINAL");
        RecordPatches.AppendBaselineTable(source, 61021, new object(), pristine);

        // Stand in for AL writing through the live row the loader just inserted.
        pristine[0][0] = new NavText(0, "MUTATED");

        Assert.Equal("ORIGINAL", depCompany.Sources[0].Tables[0].Rows[0][0].ToString());
    }

    /// <summary>
    /// Idempotent per (source, tableId). The lazy loader cannot append twice for one table —
    /// a table a baseline carries is a table the restore put in the store, so the loader never
    /// fires for it — but duplicating install-seeded rows is a bad enough outcome to guard
    /// rather than argue about.
    /// </summary>
    [Fact]
    public void AppendingTheSameTableTwice_DoesNotDuplicateItsRows()
    {
        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        RecordPatches.AppendBaselineTable(source, 61022, new object(), Rows("A", "B"));
        RecordPatches.AppendBaselineTable(source, 61022, new object(), Rows("C"));

        var table = Assert.Single(depCompany.Sources[0].Tables);
        Assert.Equal(2, table.Rows.Length);
        Assert.Equal("A", table.Rows[0][0].ToString());
    }

    /// <summary>A different DataAccessSource is a different store, so its tables are kept
    /// apart rather than merged into whichever source happened to be first.</summary>
    [Fact]
    public void TwoSources_KeepTheirOwnTables()
    {
        var first = new object();
        var second = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        RecordPatches.AppendBaselineTable(first, 61023, new object(), Rows("FIRST"));
        RecordPatches.AppendBaselineTable(second, 61023, new object(), Rows("SECOND"));

        Assert.Equal(2, depCompany.Sources.Count);
        Assert.Equal("FIRST",
            depCompany.Sources.Single(s => ReferenceEquals(s.Source, first)).Tables[0].Rows[0][0].ToString());
        Assert.Equal("SECOND",
            depCompany.Sources.Single(s => ReferenceEquals(s.Source, second)).Tables[0].Rows[0][0].ToString());
    }

    /// <summary>With no baseline registered at all — the window between an app group starting
    /// and its capture finishing — an append is a no-op rather than a crash. Anything loaded
    /// in that window is picked up by CaptureInstallBaselineSnapshot walking the live store.
    /// </summary>
    [Fact]
    public void WithNoBaselineRegistered_AppendIsANoOp()
    {
        RecordPatches.AppendBaselineTable(new object(), 61024, new object(), Rows("X"));
        Assert.Null(RecordPatches.InstallBaselineForTests);
    }
}
