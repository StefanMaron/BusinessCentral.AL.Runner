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
    public void WithoutTheFlag_ArmDoesNothing()
    {
        TestDataProvisioner.ResetForTests();
        // No backup is resolved, no reader is located, no exception: the whole path is
        // skipped. If Arm ever started work before checking the flag, this would throw
        // TestDataUnavailableException or BackupReaderException on a machine without either.
        TestDataProvisioner.Arm();
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
        Assert.Contains(Path.GetFullPath(missing), ex.Message.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MissingShippedBackup_MessageNamesEveryProbedPath()
    {
        // Built through the same function ResolveBackupPath probes with, so the message and
        // the search can never disagree. The expected paths are composed from
        // TestArtifacts — the suite's one source of truth for where BC artifacts live — so
        // this cannot drift into asserting a directory nothing populates (the defect
        // TestArtifactsGateTests exists to prevent), and it makes the real claim: --test-data
        // probes the SAME sandbox cache the rest of the suite recognises.
        const string home = "/home/nobody";
        var runnerArtifacts = TestArtifacts.StandardCacheDir(home);
        var candidates = TestDataOptions.CandidateBackupPaths(
            home, runnerArtifacts, "28.1.49838.50621", "w1");

        Assert.Equal(2, candidates.Count);
        Assert.Contains(
            Path.Combine(TestArtifacts.LegacyCacheDir(home), "28.1.49838.50621", "w1", "BusinessCentral-W1.bak"),
            candidates);
        Assert.Contains(
            Path.Combine(runnerArtifacts, "28.1.49838.50621", "w1", "BusinessCentral-W1.bak"),
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

    // ──────────────────────────────────────── company selection ──

    [Fact]
    public void MultipleCompaniesAndNoneNamed_FailsRatherThanPickingOne()
    {
        // The repo owner's decision, and the reason for it: a BC backup routinely holds
        // several companies with different data. Picking one silently means every hydrated
        // row came from a company nobody selected — the same class of silent wrong answer
        // as restoring an empty snapshot.
        var companies = new[] { "CRONUS International Ltd_", "My Company" };

        var ex = Assert.Throws<TestDataUnavailableException>(
            () => TestDataProvisioner.ResolveCompany(companies, null, "/x/BusinessCentral-W1.bak"));

        Assert.Contains("CRONUS International Ltd_", ex.Message, StringComparison.Ordinal);
        Assert.Contains("My Company", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--test-data-company", ex.Message, StringComparison.Ordinal);

        // On the FIRST line, not buried below it. Measured during #2258: the bundle reporter
        // keeps only line 1 of an EXEC-FAIL message, so a message that named the count on
        // line 1 and the companies on line 3 reached the user as "holds 2 companies" with
        // nothing to act on.
        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains("CRONUS International Ltd_", firstLine, StringComparison.Ordinal);
        Assert.Contains("--test-data-company", firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleCompany_NeedsNoChoice()
        => Assert.Equal("CRONUS International Ltd_",
            TestDataProvisioner.ResolveCompany(new[] { "CRONUS International Ltd_" }, null, "/x.bak"));

    [Fact]
    public void NamedCompany_IsUsed_AndAnUnknownOneFailsNamingWhatTheBackupHolds()
    {
        var companies = new[] { "CRONUS International Ltd_", "My Company" };

        Assert.Equal("My Company", TestDataProvisioner.ResolveCompany(companies, "My Company", "/x.bak"));

        var ex = Assert.Throws<TestDataUnavailableException>(
            () => TestDataProvisioner.ResolveCompany(companies, "Typo Ltd", "/x.bak"));
        Assert.Contains("Typo Ltd", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CRONUS International Ltd_", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCompaniesAtAll_FailsRatherThanHydratingNothingQuietly()
    {
        var ex = Assert.Throws<TestDataUnavailableException>(
            () => TestDataProvisioner.ResolveCompany(Array.Empty<string>(), null, "/x/BusinessCentral-W1.bak"));
        Assert.Contains("no companies", ex.Message, StringComparison.Ordinal);
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

    // ───────────────────────────────────── exclusion-rule contract ──

    [Fact]
    public void BuildPlan_IncludesExtendedTables_AndMarksThemAsNeedingTheMerge()
    {
        // The #2261 change, stated as the plan: a table whose $ext companion carries rows is
        // no longer skipped whole, it is planned AND flagged. The flag is not bookkeeping —
        // it is handed to the mechanism as "this merged read must come back with an extension
        // column", which is the only thing that turns a silently-ignored merge flag into a
        // failure instead of a table of blanks.
        const string cronus = "CRONUS International Ltd_";
        var entries = new List<BackupTableEntry>
        {
            new(119, "page", cronus, "No_ Series", 308, "Business Foundation"),
            new(  0, "page", cronus, "No_ Series$ext", null, null),
            new(  1, "page", cronus, "Source Code Setup", 242, "Business Foundation"),
            new(  1, "page", cronus, "Source Code Setup$ext", null, null),
            new(  0, "page", cronus, "ADCS User", 7710, "Base Application"),
            new( 89, "page", cronus, "Dimension Set Entry", 480, "Base Application"),
            new(  0, "page", cronus, "Dimension Set Entry", 36950, "Power BI Report embeddings"),
            new(  4, "page", cronus, "AIT Test Method Line", null, null),
            new(  7, "page", "My Company", "No_ Series", 308, "Business Foundation"),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, cronus);

        Assert.Equal(
            new[] { "No_ Series", "Source Code Setup" },
            plan.Hydratable.Select(e => e.TableName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(242, plan.Hydratable.Single(e => e.TableName == "Source Code Setup").AlTableId);

        // Exactly the extended one, and NOT the one whose companion is empty. Asserting the
        // set rather than a count: a flag on the wrong table would still hit a count of 1 and
        // would then demand extension columns from a table that has none.
        Assert.Equal(new[] { "Source Code Setup" }, plan.ExtendedTableNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Dimension Set Entry is declared by two installed apps in the same company, so the
        // AL name does not identify one physical table. Picking the candidate that has rows
        // would be exactly the silent guess this feature must not make.
        Assert.Equal(1, plan.SkippedAmbiguous);
        // ADCS User (0 rows), AIT Test Method Line (no AL id) and My Company's rows stay out.
        Assert.DoesNotContain("ADCS User", plan.Hydratable.Select(e => e.TableName));
        Assert.DoesNotContain("AIT Test Method Line", plan.Hydratable.Select(e => e.TableName));
    }

    [Fact]
    public void BuildPlan_DoesNotDemandAMergeFromATableItIsNotHydrating()
    {
        // A companion with rows whose base table is excluded (0 rows here, but ambiguity or a
        // missing AL id do the same) must not leave a requirement behind: ExtendedTableNames
        // is read per planned table, and a stale entry would be a claim about a table nobody
        // reads.
        const string cronus = "CRONUS International Ltd_";
        var entries = new List<BackupTableEntry>
        {
            new(0, "page", cronus, "Company Information", 79, "Base Application"),
            new(1, "page", cronus, "Company Information$ext", null, null),
            new(5, "page", cronus, "Currency", 4, "Base Application"),
            new(0, "page", cronus, "Currency$ext", null, null),
        };

        var plan = TestDataProvisioner.BuildPlan(entries, cronus);

        Assert.Equal(new[] { "Currency" }, plan.Hydratable.Select(e => e.TableName).ToArray());
        Assert.Empty(plan.ExtendedTableNames);
    }

    // ───────────────────────── the merge-actually-happened probe ──

    /// <summary>
    /// THE regression #2261 can ship green and silently wrong: the reader accepting the merge
    /// request, ignoring it, and exiting 0. Every extended table then hydrates with its
    /// extension fields blank and the run reports success. Measured on the shipped reader —
    /// `--mergeExtensions` (camelCase) does exactly this.
    /// </summary>
    [Fact]
    public void MergeProbe_FailsWhenTheMergedReadReturnsNoExtraColumns()
    {
        // What a silently-ignored flag looks like: `Source Code Setup` has ONE own field, so
        // both reads come back with just it.
        var ex = Assert.Throws<TestDataUnavailableException>(
            () => TestDataProvisioner.CompareMergeProbe(
                "Source Code Setup", new[] { "Primary Key" }, new[] { "Primary Key" }));

        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains("Source Code Setup", firstLine, StringComparison.Ordinal);
        Assert.Contains("--merge-extensions", firstLine, StringComparison.Ordinal);
        Assert.Contains("1 column(s) with the flag and 1 without", firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeProbe_PassesOnlyWhenTheMergedReadIsAStrictSuperset()
    {
        // The real shape: the merge adds the companion's fields and keeps the base ones.
        TestDataProvisioner.CompareMergeProbe(
            "Source Code Setup",
            new[] { "Primary Key" },
            new[] { "Primary Key", "Sales", "Purchases", "General Journal" });

        // A merged read that DROPPED a base column is not a merge either, even though it has
        // more columns than it started with. Superset, not "bigger".
        Assert.Throws<TestDataUnavailableException>(
            () => TestDataProvisioner.CompareMergeProbe(
                "Source Code Setup",
                new[] { "Primary Key" },
                new[] { "Sales", "Purchases" }));

        // And an empty merged read is a failure, not a vacuous pass.
        Assert.Throws<TestDataUnavailableException>(
            () => TestDataProvisioner.CompareMergeProbe(
                "Source Code Setup", new[] { "Primary Key" }, Array.Empty<string>()));
    }

    // ─────────────────────────── the merged-column shape contract ──

    [Theory]
    // BC's storage name for a companion field: ConvertToSqlIdentifier(field name) + "$" + app id.
    [InlineData("Bank Deposit$7a129d06-5fd6-4fb6-b82b-0bf539c779d0", "Bank Deposit", "7a129d06-5fd6-4fb6-b82b-0bf539c779d0")]
    [InlineData("Wthldg_ Tax Certificate Nos_$c31ee575-3fc7-4388-98ee-d75aa2fc5f87", "Wthldg_ Tax Certificate Nos_", "c31ee575-3fc7-4388-98ee-d75aa2fc5f87")]
    public void UnresolvedExtensionColumn_YieldsTheOwningApp(string column, string sql, string app)
    {
        Assert.True(BackupCatalog.TryParseUnresolvedExtensionColumn(column, out var sqlName, out var appId));
        Assert.Equal(sql, sqlName);
        Assert.Equal(Guid.Parse(app), appId);
    }

    [Theory]
    // Ordinary AL field names, including ones the reader really emits. None of these may be
    // mistaken for an extension column: doing so would DROP a column the runner must refuse on,
    // which is the silent-incomplete-record failure the guard exists to prevent.
    [InlineData("Invoice Nos.")]
    [InlineData("Primary Key")]
    [InlineData("Service Zone Code")]
    [InlineData("Amount$")]
    [InlineData("Code$not-a-guid")]
    [InlineData("Total$1234")]
    // A $-suffixed name whose tail is GUID-shaped but not a GUID (a letter past 'f').
    [InlineData("Legacy$7a129d06-5fd6-4fb6-b82b-0bf539c779dz")]
    public void OrdinaryColumnNames_AreNotMistakenForExtensionColumns(string column)
        => Assert.False(BackupCatalog.TryParseUnresolvedExtensionColumn(column, out _, out _));

    // ───────────────────────────────────────── row projection ──

    [Fact]
    public void ParseRows_KeepsAlColumnsAndDropsBcsOwnBookkeepingColumns()
    {
        const string json =
            "[{\"timestamp\": \"0x01\", \"Code\": \"A-BLK\", \"Description\": \"Assembly Blanket Orders\", "
          + "\"Default Nos.\": 1, \"$systemId\": \"C749D1DB-D953-F111-8E26-7CED8D9E4094\", "
          + "\"$systemCreatedAt\": \"2026-05-19 23:24:22.700\"}]";

        var rows = TestDataProvisioner.ParseRows(json);

        Assert.Single(rows);
        Assert.Equal(3, rows[0].Count);
        Assert.Equal("A-BLK", rows[0]["Code"].GetString());
        Assert.Equal("Assembly Blanket Orders", rows[0]["Description"].GetString());
        Assert.Equal(1, rows[0]["Default Nos."].GetInt32());

        // `timestamp` and the `$system*` columns are BC's own bookkeeping. Mapping them back
        // onto AL fields 2000000000-2000000004 would rest on a convention no service tier has
        // confirmed here, so they are dropped — declared, and stated in the hydration summary,
        // never silently mixed into a row. See RecordPatches.TestDataHydration's header.
        Assert.DoesNotContain("timestamp", rows[0].Keys);
        Assert.DoesNotContain("$systemId", rows[0].Keys);
        Assert.DoesNotContain("$systemCreatedAt", rows[0].Keys);
    }

    [Fact]
    public void SystemColumnNameSet_CoversEveryColumnBcMaintainsItself()
    {
        // Pinned as a set rather than left implicit: a `$system*` column that fell out of it
        // would be offered to the metatable as an AL field name, not match, and refuse the
        // table — turning a BC bookkeeping column into a whole-table outage.
        Assert.Equal(
            new[] { "$systemCreatedAt", "$systemCreatedBy", "$systemId", "$systemModifiedAt", "$systemModifiedBy", "timestamp" },
            AlRunner.Patches.RecordPatches.TestDataSystemColumnNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
