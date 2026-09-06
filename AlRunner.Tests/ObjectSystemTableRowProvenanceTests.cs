using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2875, the half CI cannot reach through AL: which writer owns the rows of a table the
/// runner can BOTH synthesise and load from a --test-data backup.
///
/// <para>Object (2000000001) is a real application-database SQL table, so a restored backup can
/// carry rows for it, and it is also projected from the runner's loaded-object inventory when
/// there is no database behind it. Deciding between the two by asking the in-memory store "do
/// you already hold a row?" is what #2875 is about: a store does not remember who filled it, and
/// an install-baseline restore hands out a BRAND-NEW provider carrying rows the projection
/// itself wrote, which then read as somebody else's.</para>
///
/// <para>The runner now records the fact instead. TestDataProvisioner's on-demand load calls
/// <c>RecordPatches.NoteBackupContributedRows</c> for every table it actually loaded rows into,
/// and both consumers ask that rather than the store. These tests drive exactly those calls —
/// the same entry points production uses — because the end-to-end path needs a BC database
/// backup, which CI has none of. The exclusion half IS driven end to end, in
/// ObjectSystemTableBaselineExclusionTests.</para>
///
/// <para>Shares the process-global install-baseline statics with
/// InstallBaselineAppendConcurrencyTests and TestDataBaselineAppendTests, so it joins their
/// non-parallel collection.</para>
/// </summary>
[Collection(InstallBaselineStaticsCollection.Name)]
public sealed class ObjectSystemTableRowProvenanceTests : IDisposable
{
    /// <summary>The literal ids, never the runner's own constants: a test that reads the same
    /// constant the implementation does cannot notice that constant changing.</summary>
    private const int ObjectTableId = 2000000001;
    private const int ObjectMetadataTableId = 2000000071;
    private const int AllObjTableId = 2000000038;
    private const int OrdinaryTableId = 61030;

    private readonly List<RecordPatches.BaselineSource>? _savedInstallBaseline;

    public ObjectSystemTableRowProvenanceTests()
    {
        // These tests hand AppendBaselineTable a synthetic DataAccessSource; leaving one in the
        // live per-app-group baseline would hand _mCreateTempDataAccess an object that is not a
        // DataAccessSource the next time anything restores.
        _savedInstallBaseline = RecordPatches.InstallBaselineForTests;
        RecordPatches.InstallBaselineForTests = null;
        RecordPatches.SetActiveDepCompanyBaseline(null);
        RecordPatches.ResetBackupRowProvenance();
    }

    public void Dispose()
    {
        RecordPatches.InstallBaselineForTests = _savedInstallBaseline;
        RecordPatches.SetActiveDepCompanyBaseline(null);
        RecordPatches.ResetBackupRowProvenance();
    }

    private static RecordPatches.InstallBaselineSnapshot EmptySnapshot()
        => new(new List<RecordPatches.BaselineSource>(), null, null, null);

    private static NavValue[][] Rows(params string[] values)
        => values.Select(v => new NavValue[] { new NavText(0, v) }).ToArray();

    /// <summary>
    /// The default state of every run — including every run without --test-data, which is the
    /// corpus, runner-extras, CI and the normal user configuration. Nothing has loaded rows for
    /// Object, so its rows are the runner's own projection and it must not reach a baseline: a
    /// captured projection is replayed into a fresh provider at the next boundary, and that
    /// replay is precisely what the projection cannot tell from a backup's rows.
    /// </summary>
    [Fact]
    public void WithNoBackupLoad_ObjectIsProjectionOwned_AndTheBaselineWriterRefusesIt()
    {
        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
        Assert.True(RecordPatches.IsProjectionOwnedSystemTableId(ObjectTableId));

        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        var ex = Assert.Throws<InvalidOperationException>(
            () => RecordPatches.AppendBaselineTable(source, ObjectTableId, new object(), Rows("X")));
        Assert.Contains("2000000001", ex.Message);
        Assert.Contains("#2875", ex.Message);

        // Refused means refused: nothing partial was published before the throw.
        Assert.Empty(depCompany.Sources);
    }

    /// <summary>
    /// The --test-data case. Once the on-demand load reports that the backup put rows into
    /// Object, those rows are the better answer and the table stops being projection-owned:
    /// the projection stands down, and the append that carries the backup's rows across the
    /// next boundary is allowed through rather than refused.
    ///
    /// <para>The ordering matters and is the reason the throw above stays unreachable in
    /// production: TestDataProvisioner.LoadOnDemand notes the provenance BEFORE it appends.</para>
    /// </summary>
    [Fact]
    public void AfterTheBackupLoadsRows_ObjectIsBackupOwned_AndTheAppendIsCarried()
    {
        RecordPatches.NoteBackupContributedRows(ObjectTableId);

        Assert.True(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
        Assert.False(RecordPatches.IsProjectionOwnedSystemTableId(ObjectTableId));

        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        RecordPatches.AppendBaselineTable(source, ObjectTableId, new object(), Rows("FROM-BACKUP"));

        var table = Assert.Single(Assert.Single(depCompany.Sources).Tables);
        Assert.Equal(ObjectTableId, table.TableId);
        Assert.Equal("FROM-BACKUP", table.Rows[0][0].ToString());
    }

    /// <summary>
    /// Provenance is per table, not a global "a backup is armed" flag. A backup that carries
    /// rows for some other table says nothing about Object, and the older narrowing — "does a
    /// --test-data loader exist at all" — could not make that distinction, which is why it left
    /// a residue.
    /// </summary>
    [Fact]
    public void ProvenanceIsPerTable_ARowLoadedElsewhereDoesNotClaimObject()
    {
        RecordPatches.NoteBackupContributedRows(OrdinaryTableId);

        Assert.True(RecordPatches.BackupOwnsRowsFor(OrdinaryTableId));
        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
        Assert.True(RecordPatches.IsProjectionOwnedSystemTableId(ObjectTableId));
    }

    /// <summary>
    /// Object is the ONLY table this predicate can claim. Object Metadata (2000000071) is the
    /// same shape of table, but its synthesised rows are the fixed BC-declared
    /// application-database id list plus one process-constant emit version, so a replay of it is
    /// byte-identical to a fresh projection and there is nothing for a restore to get wrong —
    /// excluding it from the baseline would be a behaviour change with no defect behind it. An
    /// ordinary business table is never projection-owned either.
    /// </summary>
    [Fact]
    public void OnlyObjectIsProjectionOwned_NotItsSiblingAndNotAnOrdinaryTable()
    {
        Assert.False(RecordPatches.IsProjectionOwnedSystemTableId(ObjectMetadataTableId));
        Assert.False(RecordPatches.IsProjectionOwnedSystemTableId(OrdinaryTableId));
        Assert.False(RecordPatches.IsProjectionOwnedSystemTableId(AllObjTableId));

        // And an ordinary table is appendable with no provenance at all — the refusal above is
        // about Object specifically, not a new gate on every append.
        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);
        RecordPatches.AppendBaselineTable(source, OrdinaryTableId, new object(), Rows("ORDINARY"));
        Assert.Equal(OrdinaryTableId, Assert.Single(Assert.Single(depCompany.Sources).Tables).TableId);
    }

    /// <summary>
    /// #2272's refusal is not weakened by any of this. AllObj is refused because it is a
    /// self-populating virtual table, and no amount of backup provenance makes it appendable —
    /// the two checks are independent, and the message still names the self-populating reason
    /// rather than the new one.
    /// </summary>
    [Fact]
    public void BackupProvenanceDoesNotUnlockASelfPopulatingVirtualTable()
    {
        RecordPatches.NoteBackupContributedRows(AllObjTableId);
        RecordPatches.SetActiveDepCompanyBaseline(EmptySnapshot());

        var ex = Assert.Throws<InvalidOperationException>(
            () => RecordPatches.AppendBaselineTable(new object(), AllObjTableId, new object(), Rows("X")));
        Assert.Contains("self-populating virtual table", ex.Message);
    }

    /// <summary>
    /// The record describes one armed backup and must not outlive it.
    /// TestDataProvisioner.ResetForTests clears it in the same breath as the loader itself; left
    /// behind, one run's backup would keep telling the next run's projection to stand down, and
    /// that run would answer for Object out of an empty table.
    /// </summary>
    [Fact]
    public void ResettingProvenance_PutsObjectBackUnderTheProjection()
    {
        RecordPatches.NoteBackupContributedRows(ObjectTableId);
        Assert.False(RecordPatches.IsProjectionOwnedSystemTableId(ObjectTableId));

        RecordPatches.ResetBackupRowProvenance();

        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
        Assert.True(RecordPatches.IsProjectionOwnedSystemTableId(ObjectTableId));
    }

    /// <summary>The provisioner's own reset is the production caller of that clear, so it is
    /// asserted through the provisioner rather than only through the primitive above — a reset
    /// that forgot the new state would leave the two halves out of step.</summary>
    [Fact]
    public void TestDataProvisionerReset_ClearsProvenanceWithTheLoader()
    {
        RecordPatches.NoteBackupContributedRows(ObjectTableId);
        RecordPatches.TestDataOnDemandLoader = static (_, _) => { };

        AlRunner.TestDataProvisioner.ResetForTests();

        Assert.Null(RecordPatches.TestDataOnDemandLoader);
        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
    }
}
