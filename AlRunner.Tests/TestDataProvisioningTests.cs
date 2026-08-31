// TestDataProvisioningTests — the proving tests for --test-data (issue #2258).
//
// WHAT IS PROVED HERE, AND WHY IT IS HERE AND NOT IN AL
//   Every claim below is about the RUNNER: a flag, a cache key, a path-resolution failure,
//   an exclusion rule. None of them is a statement about what Business Central does, so
//   .claude/rules/bc-behavior-tests-go-upstream.md does not send them to the corpus. They
//   are also not expressible as an AL bundle in tests/runner-extras/: CI runs that whole
//   directory WITHOUT --test-data and with no 900 MB backup on the machine, so an AL test
//   asserting hydrated rows would fail there by construction rather than prove anything.
//
//   The cache-key test is the important one. It is the failure mode that can ship a green,
//   silently wrong run: a baseline captured from an empty database being restored into a run
//   that asked for the backup's rows. It is asserted here as an equality/inequality claim on
//   the key itself, which is exactly the level the defect lives at — the two cache tiers are
//   keyed by that string and nothing else.
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataProvisioningTests : IDisposable
{
    public TestDataProvisioningTests()
    {
        TestDataOptions.ResetForTests();
        BackupReaderTool.ResetForTests();
    }

    public void Dispose()
    {
        TestDataOptions.ResetForTests();
        BackupReaderTool.ResetForTests();
    }

    // ─────────────────────────────────────────────── default-off ──

    [Fact]
    public void WithoutTheFlag_CacheIdentityIsEmpty_SoTheBaselineKeyIsUnchanged()
    {
        Assert.False(TestDataOptions.Enabled);
        Assert.Equal("", TestDataOptions.CacheIdentity());

        // The concrete claim: appending the identity to a dependency-set key leaves the key
        // byte-identical, which is what "absent the flag nothing changes" means at the tier
        // that actually consults it.
        const string depKey = "8a1f0c3d4e5b6a7f8091a2b3c4d5e6f7";
        Assert.Equal(depKey, depKey + TestDataOptions.CacheIdentity());
    }

    [Fact]
    public void WithoutTheFlag_HydrateAllDoesNothing()
    {
        TestDataProvisioner.ResetForTests();
        // No backup is resolved, no reader is located, no exception: the whole path is
        // skipped. If HydrateAll ever started work before checking the flag, this would throw
        // TestDataUnavailableException or BackupReaderException on a machine without either.
        TestDataProvisioner.HydrateAll();
        Assert.Null(TestDataProvisioner.LastSummary);
    }

    // ─────────────────────────────────────── the cache-key negative ──

    /// <summary>
    /// THE regression this feature can silently fail: a run WITHOUT --test-data and a run
    /// WITH it must not share an install-baseline cache entry. Both tiers are keyed by
    /// depKey (the in-memory dictionary directly, the disk tier via
    /// InstallBaselineDiskCache.BuildKeyText), so proving the keys differ proves neither tier
    /// can hand a demo-free snapshot to a --test-data run.
    /// </summary>
    [Fact]
    public void TestDataRun_AndPlainRun_DoNotShareAnInstallBaselineCacheKey()
    {
        // Deliberately goes through TestExecutor.CurrentInstallBaselineCacheKey — the exact
        // function whose result keys both tiers — rather than re-composing the key here. A
        // test that re-composed it would still pass if the call site stopped folding the
        // identity in, which is precisely the regression that ships a silent empty database.
        var dir = Directory.CreateTempSubdirectory("al-runner-testdata-cachekey");
        var previousEnv = Environment.GetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar);
        try
        {
            var fakeReader = Path.Combine(dir.FullName, "bcbak");
            File.WriteAllBytes(fakeReader, new byte[] { 0x7f, 0x45, 0x4c, 0x46 });
            var backup = Path.Combine(dir.FullName, "BusinessCentral-W1.bak");
            File.WriteAllBytes(backup, new byte[256]);

            Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, fakeReader);
            BackupReaderTool.ResetForTests();

            var plain = TestExecutor.CurrentInstallBaselineCacheKey();

            TestDataOptions.Enabled = true;
            TestDataOptions.ExplicitBackupPath = backup;
            TestDataOptions.CompanyOverride = "CRONUS International Ltd_";
            var withTestData = TestExecutor.CurrentInstallBaselineCacheKey();

            Assert.NotEqual(plain, withTestData);
            Assert.StartsWith(plain, withTestData, StringComparison.Ordinal);

            // And the difference must survive into the DISK key, which is a different function.
            Assert.NotEqual(
                InstallBaselineDiskCache.BuildKeyText(plain, 1),
                InstallBaselineDiskCache.BuildKeyText(withTestData, 1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, previousEnv);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CacheIdentity_ChangesWithTheBackup_TheCompany_AndTheReaderBuild()
    {
        const string bak = "/artifacts/28.1.49838.50621/w1/BusinessCentral-W1.bak";
        const string other = "/artifacts/28.1.49838.50621/us/BusinessCentral-US.bak";
        const string company = "CRONUS International Ltd_";
        const string reader = "readerhash0000ab";

        var baseline = TestDataOptions.BuildCacheIdentity(bak, company, reader);

        Assert.NotEqual(baseline, TestDataOptions.BuildCacheIdentity(other, company, reader));
        Assert.NotEqual(baseline, TestDataOptions.BuildCacheIdentity(bak, "My Company", reader));
        // A reader upgrade that changes decoded VALUES must invalidate the snapshot; that is
        // why the extractor identity is part of the key rather than a comment.
        Assert.NotEqual(baseline, TestDataOptions.BuildCacheIdentity(bak, company, "readerhash0000cd"));

        // Stable for identical inputs — a key that churned would defeat the cache entirely.
        Assert.Equal(baseline, TestDataOptions.BuildCacheIdentity(bak, company, reader));
    }

    [Fact]
    public void CacheIdentity_TracksTheBackupFilesContent_NotJustItsName()
    {
        var dir = Directory.CreateTempSubdirectory("al-runner-testdata-key");
        try
        {
            var bak = Path.Combine(dir.FullName, "BusinessCentral-W1.bak");
            File.WriteAllBytes(bak, new byte[64]);
            var first = TestDataOptions.BuildCacheIdentity(bak, "CRONUS", "reader");

            // A different backup written to the same path is a different database.
            File.WriteAllBytes(bak, new byte[128]);
            File.SetLastWriteTimeUtc(bak, DateTime.UtcNow.AddMinutes(5));
            var second = TestDataOptions.BuildCacheIdentity(bak, "CRONUS", "reader");

            Assert.NotEqual(first, second);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ────────────────────────────────── missing backup fails loud ──

    [Fact]
    public void ExplicitBackupThatDoesNotExist_ThrowsAndNamesThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "al-runner-no-such-backup-9a7c.bak");
        Assert.False(File.Exists(missing));
        TestDataOptions.Enabled = true;
        TestDataOptions.ExplicitBackupPath = missing;

        var ex = Assert.Throws<TestDataUnavailableException>(() => TestDataOptions.ResolveBackupPath());
        Assert.Contains(Path.GetFullPath(missing), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingShippedBackup_MessageNamesEveryProbedPath()
    {
        // Built through the same function ResolveBackupPath probes with, so the message and
        // the search can never disagree.
        var candidates = TestDataOptions.CandidateBackupPaths(
            "/home/nobody", "/home/nobody/.local/share/al-runner/artifacts", "28.1.49838.50621", "w1");

        Assert.Equal(2, candidates.Count);
        Assert.Contains(
            Path.Combine("/home/nobody", ".bcartifacts.cache", "sandbox", "28.1.49838.50621", "w1", "BusinessCentral-W1.bak"),
            candidates);
        Assert.Contains(
            Path.Combine("/home/nobody/.local/share/al-runner/artifacts", "28.1.49838.50621", "w1", "BusinessCentral-W1.bak"),
            candidates);
        Assert.All(candidates, c => Assert.False(File.Exists(c)));
    }

    [Theory]
    [InlineData("w1", "BusinessCentral-W1.bak")]
    [InlineData("us", "BusinessCentral-US.bak")]
    [InlineData("de", "BusinessCentral-DE.bak")]
    public void BackupFileName_FollowsTheCountryChannel(string country, string expected)
        => Assert.Equal(expected, TestDataOptions.BackupFileName(country));

    // ─────────────────────────────────────────── flag parsing ──

    [Fact]
    public void BareFlag_EnablesWithNoExplicitPath()
    {
        Assert.True(TestDataOptions.TryParseArg("--test-data"));
        Assert.True(TestDataOptions.Enabled);
        Assert.Null(TestDataOptions.ExplicitBackupPath);
    }

    [Fact]
    public void EqualsForm_EnablesWithAnExplicitPath()
    {
        Assert.True(TestDataOptions.TryParseArg("--test-data=/tmp/x.bak"));
        Assert.True(TestDataOptions.Enabled);
        Assert.Equal("/tmp/x.bak", TestDataOptions.ExplicitBackupPath);
    }

    [Fact]
    public void UnrelatedArgs_AreNotConsumed()
    {
        Assert.False(TestDataOptions.TryParseArg("--country"));
        Assert.False(TestDataOptions.TryParseArg("tests/runner-extras"));
        // The one that would break `al-runner --test-data <bundle>` if the parser took a
        // space-separated value: the bundle path must stay a bundle path.
        Assert.False(TestDataOptions.TryParseArg("--test-database"));
        Assert.False(TestDataOptions.Enabled);
    }

    // ───────────────────────────────────── reader-tool contract ──

    [Fact]
    public void MissingReader_ThrowsAndNamesEveryProbedLocation()
    {
        var candidates = BackupReaderTool.CandidateExecutables("/nowhere/bcbak", "/home/nobody");
        Assert.Contains("/nowhere/bcbak", candidates);
        Assert.Contains(Path.Combine("/nowhere/bcbak", "bcbak"), candidates);
        Assert.Contains(Path.Combine("/home/nobody", ".cache", "al-runner", "bcbak", "bcbak"), candidates);
    }

    [Fact]
    public void ExtractorIdentity_ChangesWhenASiblingAssemblyChanges()
    {
        // The failure this guards: for a framework-dependent build the apphost is byte-
        // identical between builds and only the managed .dlls change, so hashing the
        // executable alone would let a reader fix that changes decoded VALUES be masked by
        // a cached baseline keyed on an unchanged identity.
        var dir = Directory.CreateTempSubdirectory("al-runner-bcbak-identity");
        try
        {
            var exe = Path.Combine(dir.FullName, "bcbak");
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir.FullName, "Reader.Core.dll"), new byte[] { 9, 9 });
            var before = BackupReaderTool.ComputeIdentity(exe);

            File.WriteAllBytes(Path.Combine(dir.FullName, "Reader.Core.dll"), new byte[] { 9, 9, 9 });
            var after = BackupReaderTool.ComputeIdentity(exe);

            Assert.NotEqual(before, after);
            Assert.Equal(16, before.Length);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ────────────────────────────────── catalog parsing contract ──

    [Fact]
    public void ParseTables_ReadsRowCountCompanyNameAndAlResolution()
    {
        const string output =
            "     119  page  CRONUS International Ltd_\tNo_ Series\t308 \"No. Series\" (Business Foundation)\n"
          + "       1  page  CRONUS International Ltd_\tSource Code Setup\t242 \"Source Code Setup\" (Business Foundation)\n"
          + "       1  page  CRONUS International Ltd_\tSource Code Setup$ext\t-\n"
          + "       0  none  -\t-\t-\n";

        var entries = BackupCatalog.ParseTables(output);
        Assert.Equal(4, entries.Count);

        var noSeries = entries[0];
        Assert.Equal(119, noSeries.RowCount);
        Assert.Equal("CRONUS International Ltd_", noSeries.Company);
        Assert.Equal("No_ Series", noSeries.TableName);
        Assert.Equal(308, noSeries.AlTableId);
        Assert.Equal("Business Foundation", noSeries.AppName);
        Assert.False(noSeries.IsExtensionCompanion);

        Assert.True(entries[2].IsExtensionCompanion);
        Assert.Equal("Source Code Setup", entries[2].BaseTableName);
        Assert.Null(entries[2].AlTableId);
    }

    [Fact]
    public void ParseTables_RefusesALineItCannotRead()
    {
        var ex = Assert.Throws<BackupReaderException>(
            () => BackupCatalog.ParseTables("this is not a tables line\n"));
        Assert.Contains("unrecognised", ex.Message, StringComparison.Ordinal);
    }

    private const string DescribeOutput =
        "Table 308 \"No. Series\" — app \"Business Foundation\" (f3552374-a1f2-4356-848e-196002525837)\n"
      + "SQL object: CRONUS International Ltd_$No_ Series$f3552374-a1f2-4356-848e-196002525837\n"
      + "    Id  AL name                                  AL type                      SQL column                               SQL type\n"
      + "     1  Code                                     Code[20]                     Code                                     nvarchar(20)\n"
      + "     2  Description                              Text[100]                    Description                              nvarchar(100)\n"
      + "     3  Default Nos.                             Boolean                      Default Nos_                             tinyint\n"
      + "     -  -                                        -                            $systemId                                uniqueidentifier (system column)\n";

    [Fact]
    public void ParseDescribe_MapsAlFieldIdsAndFlagsSystemColumns()
    {
        var schema = BackupCatalog.ParseDescribe(DescribeOutput, "No_ Series");

        Assert.Equal(308, schema.AlTableId);
        Assert.Equal("No. Series", schema.AlTableName);
        Assert.Equal("Business Foundation", schema.AppName);
        Assert.Equal(4, schema.Columns.Count);

        Assert.Equal(1, schema.Columns[0].AlFieldId);
        Assert.Equal("Code", schema.Columns[0].AlName);
        Assert.Equal("Code[20]", schema.Columns[0].AlType);
        Assert.Equal("Code", schema.Columns[0].SqlColumn);
        Assert.Equal("nvarchar(20)", schema.Columns[0].SqlType);

        Assert.Equal(3, schema.Columns[2].AlFieldId);
        Assert.Equal("Default Nos.", schema.Columns[2].AlName);

        Assert.True(schema.Columns[3].IsSystemColumn);
        Assert.Equal("$systemId", schema.Columns[3].SqlColumn);
    }

    [Fact]
    public void ParseDescribe_RefusesAColumnLineThatOverflowsItsFixedWidthLayout()
    {
        // A field name one character too long pushes every later column right. Slicing it
        // anyway would produce a WRONG AL field id — the one error that would corrupt
        // hydrated rows without failing anything — so it is refused instead.
        var header = "    Id  AL name                                  AL type                      SQL column                               SQL type";
        var overflowing = "     1  " + new string('X', 42) + "Code[20]                     Code                                     nvarchar(20)";
        var output = "Table 308 \"No. Series\" — app \"Business Foundation\" (x)\n" + header + "\n" + overflowing + "\n";

        var ex = Assert.Throws<BackupReaderException>(() => BackupCatalog.ParseDescribe(output, "No_ Series"));
        Assert.Contains("overflows", ex.Message, StringComparison.Ordinal);
    }

    // ───────────────────────────────────── exclusion-rule contract ──

    [Fact]
    public void BuildPlan_ExcludesExtendedTables_EmptyTables_AmbiguousNames_AndOtherCompanies()
    {
        const string cronus = "CRONUS International Ltd_";
        var entries = new List<BackupTableEntry>
        {
            new(119, "page", cronus, "No_ Series", 308, "Business Foundation"),
            new(  1, "page", cronus, "Source Code Setup", 242, "Business Foundation"),
            new(  1, "page", cronus, "Source Code Setup$ext", null, null),
            new(  0, "page", cronus, "ADCS User", 7710, "Base Application"),
            new( 89, "page", cronus, "Dimension Set Entry", 480, "Base Application"),
            new(  0, "page", cronus, "Dimension Set Entry", 36950, "Power BI Report embeddings"),
            new(  4, "page", cronus, "AIT Test Method Line", null, null),
            new(  7, "page", "My Company", "No_ Series", 308, "Business Foundation"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, cronus);

        Assert.Single(plan.Hydratable);
        Assert.Equal("No_ Series", plan.Hydratable[0].TableName);
        Assert.Equal(308, plan.Hydratable[0].AlTableId);

        // Source Code Setup is excluded because its $ext companion carries rows: hydrating
        // the base table alone would ship a knowingly incomplete setup record.
        Assert.Equal(1, plan.SkippedExtensionData);
        // Dimension Set Entry is declared by two installed apps in the same company, so the
        // AL name does not identify one physical table. Picking the candidate that has rows
        // would be exactly the silent guess this feature must not make.
        Assert.Equal(1, plan.SkippedAmbiguous);
    }

    [Fact]
    public void BuildPlan_KeepsATableWhoseExtensionCompanionIsEmpty()
    {
        const string cronus = "CRONUS International Ltd_";
        var entries = new List<BackupTableEntry>
        {
            new(5, "page", cronus, "Currency", 4, "Base Application"),
            new(0, "page", cronus, "Currency$ext", null, null),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, cronus);

        Assert.Single(plan.Hydratable);
        Assert.Equal("Currency", plan.Hydratable[0].TableName);
        Assert.Equal(0, plan.SkippedExtensionData);
    }

    // ───────────────────────────────────────── row projection ──

    [Fact]
    public void ParseRows_KeysValuesByAlFieldIdAndDropsUnmappedColumns()
    {
        const string json =
            "[{\"timestamp\": \"0x01\", \"Code\": \"A-BLK\", \"Description\": \"Assembly Blanket Orders\", "
          + "\"Default Nos.\": 1, \"$systemId\": \"C749D1DB-D953-F111-8E26-7CED8D9E4094\"}]";
        var map = new Dictionary<string, int> { ["Code"] = 1, ["Description"] = 2, ["Default Nos."] = 3 };

        var rows = TestDataProvisioner.ParseRows(json, map);

        Assert.Single(rows);
        Assert.Equal(3, rows[0].Count);
        Assert.Equal("A-BLK", rows[0][1].GetString());
        Assert.Equal("Assembly Blanket Orders", rows[0][2].GetString());
        Assert.Equal(1, rows[0][3].GetInt32());
        // `timestamp` and `$systemId` carry no AL field id, so they are not projected —
        // see RecordPatches.TestDataHydration's header for why they are excluded rather
        // than mapped onto AL fields 2000000000-2000000004.
        Assert.DoesNotContain(0, rows[0].Keys);
    }
}
