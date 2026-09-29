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
// TRAP — SetupVersion. On a service tier it is bumped by PermissionSetupMonitor.ResetSetup, which
// TableChangeMonitorCollection runs off CacheSynchronization.TableDataChanged (committed changes
// to the permission tables, delivered by cache synchronisation), and by company-table writes
// (SystemTableTriggers.OnWriteToCompanyTable). The runner has neither channel, so its version
// stays 0 and only a different set's computation evicts the slot. Tracked in #4983.
using System;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

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
