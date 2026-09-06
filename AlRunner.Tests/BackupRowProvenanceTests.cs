using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Which writer put rows into a table the runner can load from a --test-data backup, and what
/// the install-baseline writer does about it.
///
/// <para>WHAT CHANGED, AND WHY THIS FILE IS SMALLER THAN ITS SUBJECT USED TO BE (#3071). This
/// was ObjectSystemTableRowProvenanceTests, for #2875. Object (2000000001) was then the one
/// table the runner could BOTH synthesise rows for — projecting its loaded-object inventory
/// when there was no database behind it — and load from a backup, so something had to decide
/// between the two writers. Asking the in-memory store "do you already hold a row?" cannot: a
/// store does not remember who filled it, and an install-baseline restore hands out a
/// BRAND-NEW provider carrying rows the projection itself wrote, which then read as somebody
/// else's. #2875 recorded the fact at the writer instead, and had the baseline REFUSE a
/// projection-owned Object.</para>
///
/// <para>Corpus codeunit 61202 (StefanMaron/BusinessCentral.AL.Language.Tests#197) has since
/// measured 2000000001 on seven BC OnPrem legs and found it empty, so the projection is gone
/// and with it both the ambiguity and the refusal: the only rows that table can hold now are a
/// backup's, and those are exactly the rows a baseline SHOULD carry across a boundary.
/// <c>IsProjectionOwnedSystemTableId</c> was deleted — it was defined as "2000000001 with no
/// backup behind it", which now describes an empty table rather than a projection.</para>
///
/// <para>WHAT SURVIVES, AND WHY IT IS NOT DEAD CODE. The recorder does.
/// <c>TestDataProvisioner.LoadOnDemand</c> is the only writer that can put rows into these
/// tables and it is the only place that fact exists; nothing downstream of a store can
/// reconstruct it. Issue #3236 is its named consumer — the same wrong-shaped question,
/// <c>ProviderHasAnyRow</c>, still decides whether Object Metadata's (2000000071) #2771 payload
/// refusal is armed, and an install-baseline restore replaying that table's synthesised rows
/// disarms it. These tests drive the recorder's own entry points, the ones production uses,
/// because the end-to-end path needs a BC database backup and CI has none. The inverted
/// baseline claim IS driven end to end, in ObjectSystemTableEmptyRowSetTests.</para>
///
/// <para>Shares the process-global install-baseline statics with
/// InstallBaselineAppendConcurrencyTests and TestDataBaselineAppendTests, so it joins their
/// non-parallel collection.</para>
/// </summary>
[Collection(InstallBaselineStaticsCollection.Name)]
public sealed class BackupRowProvenanceTests : IDisposable
{
    /// <summary>The literal ids, never the runner's own constants: a test that reads the same
    /// constant the implementation does cannot notice that constant changing.</summary>
    private const int ObjectTableId = 2000000001;
    private const int ObjectMetadataTableId = 2000000071;
    private const int AllObjTableId = 2000000038;
    private const int OrdinaryTableId = 61030;

    private readonly List<RecordPatches.BaselineSource>? _savedInstallBaseline;

    public BackupRowProvenanceTests()
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
    /// THE INVERTED CLAIM (#3071). The default state of every run — including every run without
    /// --test-data, which is the corpus, runner-extras, CI and the normal user configuration.
    /// Nothing has loaded rows for Object, and the baseline writer must now CARRY an append for
    /// it rather than throw.
    ///
    /// <para>#2875's refusal was correct for as long as the runner projected rows into this
    /// table: a captured projection is replayed into a fresh provider at the next boundary, and
    /// that replay is what the projection could not tell from a backup's rows. With no
    /// projection there is nothing to mistake, and a refusal here would be a live defect rather
    /// than a guard — it would drop a --test-data backup's real rows on the floor in exactly
    /// the case a disk-cache hit restores them in a process whose on-demand loader never
    /// ran.</para>
    /// </summary>
    [Fact]
    public void WithNoBackupLoad_TheBaselineWriterCarriesObject_RatherThanRefusingIt()
    {
        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));

        var source = new object();
        var depCompany = EmptySnapshot();
        RecordPatches.SetActiveDepCompanyBaseline(depCompany);

        RecordPatches.AppendBaselineTable(source, ObjectTableId, new object(), Rows("CARRIED"));

        var table = Assert.Single(Assert.Single(depCompany.Sources).Tables);
        Assert.Equal(ObjectTableId, table.TableId);
        Assert.Equal("CARRIED", table.Rows[0][0].ToString());
    }

    /// <summary>
    /// The --test-data case, unchanged by #3071 and still the case that matters: rows the
    /// backup actually loaded are carried across the next boundary.
    ///
    /// <para>TestDataProvisioner.LoadOnDemand notes the provenance BEFORE it appends, so this
    /// is the ordering production uses.</para>
    /// </summary>
    [Fact]
    public void AfterTheBackupLoadsRows_ObjectIsBackupOwned_AndTheAppendIsCarried()
    {
        RecordPatches.NoteBackupContributedRows(ObjectTableId);

        Assert.True(RecordPatches.BackupOwnsRowsFor(ObjectTableId));

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
    /// a residue. #3236 needs exactly this granularity.
    /// </summary>
    [Fact]
    public void ProvenanceIsPerTable_ARowLoadedElsewhereDoesNotClaimObject()
    {
        RecordPatches.NoteBackupContributedRows(OrdinaryTableId);

        Assert.True(RecordPatches.BackupOwnsRowsFor(OrdinaryTableId));
        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectMetadataTableId));
    }

    /// <summary>
    /// SIBLING PARITY (#3071). Object (2000000001) and Object Metadata (2000000071) are the two
    /// real application-database system tables the on-demand loader can reach, and they are now
    /// treated identically by the baseline writer: both appendable with no provenance recorded
    /// at all. Object was the odd one out only for as long as it carried a projection.
    ///
    /// <para>Asserted with an ordinary business table alongside, so this reads as "no special
    /// case remains" rather than "one special case was swapped for another".</para>
    /// </summary>
    [Fact]
    public void ObjectAndItsSiblingAndAnOrdinaryTable_AreAllAppendableWithoutProvenance()
    {
        foreach (var tableId in new[] { ObjectTableId, ObjectMetadataTableId, OrdinaryTableId })
        {
            var depCompany = EmptySnapshot();
            RecordPatches.SetActiveDepCompanyBaseline(depCompany);

            RecordPatches.AppendBaselineTable(new object(), tableId, new object(), Rows("ROW"));

            Assert.Equal(tableId, Assert.Single(Assert.Single(depCompany.Sources).Tables).TableId);
        }
    }

    /// <summary>
    /// #2272's refusal is not weakened by any of this, and #3071 did not touch it. AllObj is
    /// refused because it is a self-populating virtual table with no SQL behind it, and no
    /// amount of backup provenance makes it appendable — the message still names the
    /// self-populating reason.
    ///
    /// <para>This is the arm that fails if removing Object's special case had been done by
    /// loosening the shared guard instead of by deleting one disjunct from it.</para>
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
    /// behind, one run's backup would speak for the next run's.
    /// </summary>
    [Fact]
    public void ResettingProvenance_ForgetsWhatTheBackupLoaded()
    {
        RecordPatches.NoteBackupContributedRows(ObjectTableId);
        Assert.True(RecordPatches.BackupOwnsRowsFor(ObjectTableId));

        RecordPatches.ResetBackupRowProvenance();

        Assert.False(RecordPatches.BackupOwnsRowsFor(ObjectTableId));
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
