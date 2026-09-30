// RecordPatches.PermissionSystemTable — the three PermissionDataProviderBase tables:
//   2000000005 "Permission", 2000000251 "Metadata Permission", 2000000254 "Expanded Permission".
//
// They are served by BC's own DataAccess over BC's own provider, built by BC's own
// DataAccessSource.GetVirtualDataAccess — the path Date and Integer take (#3506, #3485). So which
// sets are listed, include expansion, tenant sets, filtering and row layout are all BC's code, and
// so is PermissionDataProviderBase.GetPermissions' memo: one slot per provider instance, keyed on
// PermissionSetupMonitor.SetupVersion, with the instance cached per DataAccessSource per table.
// That memo is observable from AL: after a tenant grant is inserted, a re-read of an already
// composed set answers the composition from before the insert (corpus 67945,
// ExpandedPermission_OpenVariable_*, red on every cloud leg until it asserted that; #2910, #3705).
//
// The runner contributes two inputs and computes no permission itself:
//   - the permission-set inventory BC's GetMetadataPermissionSets reads
//     (EnsurePermissionMetadataPopulated, #2893 / #3609);
//   - a real PermissionSetupMonitor on the skeleton NavDatabase, built by BC's own constructor
//     (PlantPermissionSetupMonitor), which GetPermissions dereferences for its memo key.
//
// SetupVersion. BC bumps it from SystemTableTriggers.OnTransactionEnded (commit AND rollback):
// every system table written in the transaction goes to TableChangeMonitors.NotifyTableChanges,
// which runs PermissionSetupMonitor.ResetSetup for the ones in its TableIds. The runner mirrors
// that at its transaction ends, including a transaction world that ends without committing
// (NotePermissionSetupTableWrite / EndPermissionSetupTransaction, #4983, #5022; corpus 67947).
// BC also bumps mid-transaction, from SystemTableTriggers.OnWriteToCompanyTable on a Company
// insert, rename or delete; the runner mirrors insert and delete at its data-layer prepends
// (#5020). Rename is not mirrored: the runner refuses a Company rename before it gets there (#5071).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int PermissionSystemTableId = 2000000005;
    internal const int MetadataPermissionSystemTableId = 2000000251;
    internal const int ExpandedPermissionSystemTableId = 2000000254;

    internal static bool IsPermissionFamilyTableId(int tableId)
        => tableId == PermissionSystemTableId
           || tableId == MetadataPermissionSystemTableId
           || tableId == ExpandedPermissionSystemTableId;

    private static bool IsPermissionFamilyTable(NCLMetaTable? table)
        => table != null && IsPermissionFamilyTableId(table.TableId);

    private static string PermissionFamilyApi(int tableId) => tableId switch
    {
        PermissionSystemTableId => "Permission (system table 2000000005)",
        MetadataPermissionSystemTableId => "Metadata Permission (system table 2000000251)",
        _ => "Expanded Permission (system table 2000000254)",
    };

    /// <summary>The PermissionSetupMonitor planted on the skeleton NavDatabase, or null.</summary>
    private static object? _plantedPermissionSetupMonitor;

    /// <summary>
    /// BC's own DataAccess for a PermissionDataProviderBase table. Refuses, naming the table,
    /// when the skeleton database carries no PermissionSetupMonitor: BC's GetPermissions would
    /// otherwise raise an anonymous NullReferenceException that an asserterror absorbs.
    /// </summary>
    private static object GetPermissionFamilyDataAccess(object dataAccessSource, NCLMetaTable table)
    {
        if (_plantedPermissionSetupMonitor == null)
            throw new BcShapeGapException(PermissionFamilyApi(table.TableId),
                "NavDatabase.PermissionSetupMonitor",
                "no PermissionSetupMonitor could be built on the skeleton NavDatabase, and BC's "
                + "PermissionDataProviderBase.GetPermissions reads its SetupVersion on every request");

        // The inventory BC's GetMetadataPermissionSets reads; without it every table lists nothing.
        EnsurePermissionMetadataPopulated();

        return GetBcVirtualDataAccess(dataAccessSource, table,
            "every read of " + PermissionFamilyApi(table.TableId) + " would answer from an empty store");
    }

    /// <summary>Monitored tables written since the last transaction end — BC's
    /// SystemTableTriggers.changedTables, narrowed to the one monitor the runner plants.</summary>
    private static readonly HashSet<int> _changedPermissionSetupTables = new();

    /// <summary>A non-temporary write to <paramref name="tableId"/>, from the runner's per-table
    /// write note. Remembered only when the planted monitor watches that table.</summary>
    internal static void NotePermissionSetupTableWrite(int tableId)
        => NotePermissionSetupTableWrite(_plantedPermissionSetupMonitor as TableChangeMonitor,
            _changedPermissionSetupTables, tableId);

    internal static void NotePermissionSetupTableWrite(TableChangeMonitor? monitor, ISet<int> changed, int tableId)
    {
        if (monitor != null && monitor.TableIds.Contains(tableId))
            changed.Add(tableId);
    }

    /// <summary>
    /// A transaction ended, committed or rolled back: BC's SystemTableTriggers.OnTransactionEnded
    /// notifies the monitors of the tables written in it, and PermissionSetupMonitor.ResetSetup
    /// then advances SetupVersion, so PermissionDataProviderBase.GetPermissions recomposes.
    ///
    /// Observably equivalent: ResetSetup is IncrementSetupVersion plus
    /// Database.SecurityAndLicense.ClearPermissions(false), which clears the role cache of a
    /// NavDatabaseSecurityAndLicense the skeleton database does not have (null) — so the bump is
    /// the whole AL-visible effect. Its 2000000004/2000000005 branch resets every tenant's
    /// monitor; the runner has one. Corpus 67947 pins Commit, asserterror and test-boundary reads.
    /// TRAP: calling ResetSetup itself NREs on that null SecurityAndLicense.
    /// </summary>
    internal static void EndPermissionSetupTransaction()
        => EndPermissionSetupTransaction(_plantedPermissionSetupMonitor as TableChangeMonitor,
            _changedPermissionSetupTables);

    /// <summary>Returns whether the version was advanced.</summary>
    internal static bool EndPermissionSetupTransaction(TableChangeMonitor? monitor, ISet<int> changed)
    {
        if (changed.Count == 0) return false;
        changed.Clear();
        if (monitor == null) return false;
        monitor.IncrementSetupVersion();
        return true;
    }

    /// <summary>
    /// Prepended to RecordImplementation.InsertRecordAsync(DataError), on its parentRecord.
    /// BC's OnAfterInsertCompany ends in OnWriteToCompanyTable, which advances SetupVersion at
    /// once, inside the transaction (#5020). Only an insert that lands reaches it, so a row
    /// already under this name — the `if Company.Insert()` duplicate that answers false —
    /// does not advance it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnCompanyInsertRecord(object? record)
    {
        if (!IsCompanyRow(record, out var company)) return;
        if (CompanyRowExists(company, company.GetFieldValue(CompanyNameFieldNo))) return;
        ResetPermissionSetupForCompanyWrite();
    }

    /// <summary>
    /// Prepended to RecordImplementation.DeleteRecordAsync(DataError), on its parentRecord: the
    /// `2000000006` arm of BC's OnBeforeDeleteAsync ends in OnWriteToCompanyTable. Guarded on
    /// the row existing, because a prepend runs before the delete does.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnCompanyDeleteRecord(object? record)
    {
        if (!IsCompanyRow(record, out var company)) return;
        if (!CompanyRowExists(company, company.GetFieldValue(CompanyNameFieldNo))) return;
        ResetPermissionSetupForCompanyWrite();
    }

    /// <summary>Company's primary key, "Name" — the field BC's Company arms read by number.</summary>
    private const int CompanyNameFieldNo = 1;

    private static bool IsCompanyRow(object? record, out NavRecord company)
    {
        company = null!;
        if (record is not NavRecord { IsTemporary: false } rec) return false;
        if (rec.MetaTable?.TableId != CompanySystemTableId) return false;
        company = rec;
        return true;
    }

    private static bool CompanyRowExists(NavRecord company, NavValue? name)
    {
        if (name == null) return false;
        var session = company.ParentSession;
        if (session == null) return false;
        using var probe = new NavRecord(session, CompanySystemTableId, SecurityFiltering.Ignored);
        // CS0618: sync-over-async; a Cecil prepend is a void method with no await point.
#pragma warning disable CS0618
        return probe.ALGet(DataError.TrapError, name);
#pragma warning restore CS0618
    }

    /// <summary>
    /// BC's OnWriteToCompanyTable: PermissionSetupMonitor.ResetPermissionSetup(false), which is
    /// IncrementSetupVersion plus SecurityAndLicense.ClearPermissions — the second a no-op the
    /// skeleton cannot run (null SecurityAndLicense), as for EndPermissionSetupTransaction.
    /// </summary>
    private static void ResetPermissionSetupForCompanyWrite()
        => (_plantedPermissionSetupMonitor as TableChangeMonitor)?.IncrementSetupVersion();

    /// <summary>
    /// Build a real PermissionSetupMonitor through BC's own constructor and store it on the
    /// skeleton NavDatabase, where BC's NavDatabase ctor would have put it. GetUninitializedObject
    /// skipped that ctor. Returns the monitor now on the database, or null, having changed
    /// nothing, if BC's shape moved.
    /// </summary>
    internal static object? PlantPermissionSetupMonitor(object skeletonDatabase)
    {
        var tDatabase = skeletonDatabase.GetType();
        var field = tDatabase.GetField("<PermissionSetupMonitor>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var tMonitor = tDatabase.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.PermissionSetupMonitor");
        var ctor = tMonitor?.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, new[] { tDatabase }, modifiers: null);
        if (field == null || ctor == null) return null;

        var existing = field.GetValue(skeletonDatabase);
        if (existing == null)
        {
            existing = ctor.Invoke(new[] { skeletonDatabase });
            FieldPoke.SetInstance(field, skeletonDatabase, existing);
        }
        return existing;
    }
}
