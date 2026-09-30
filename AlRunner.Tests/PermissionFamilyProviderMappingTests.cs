// PermissionFamilyProviderMappingTests — the runner-side wiring behind #2910 and #3705.
//
// What BC answers for "Permission" (2000000005), "Metadata Permission" (2000000251) and
// "Expanded Permission" (2000000254) is adjudicated upstream by corpus codeunits 60604 and
// 67945. What is pinned here is the runner's own part: which tables it hands to BC's own
// virtual DataAccess, the PermissionSetupMonitor it plants so BC's GetPermissions memo runs, and
// the transaction-end SetupVersion bump that invalidates that memo (#4983, corpus 67947).
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class PermissionFamilyProviderMappingTests
{
    private const string Rt = "Microsoft.Dynamics.Nav.Runtime.";

    [Fact]
    public void TheFamily_IsExactlyTheTablesBcServesThroughAPermissionDataProviderBase()
    {
        // The population, read from BC: the constant each concrete PermissionDataProviderBase's
        // TableId getter returns, read from its IL. A fourth provider in a later BC reds this.
        var location = typeof(NCLMetaTable).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(location);
        var bcTableIds = module.Types
            .Where(t => !t.IsAbstract && t.BaseType?.FullName == Rt + "PermissionDataProviderBase")
            .Select(t => (int)t.Properties.Single(p => p.Name == "TableId").GetMethod.Body.Instructions
                .Single(i => i.OpCode == OpCodes.Ldc_I4).Operand)
            .OrderBy(i => i).ToArray();
        Assert.NotEmpty(bcTableIds);

        foreach (var id in bcTableIds)
            Assert.True(RecordPatches.IsPermissionFamilyTableId(id), $"{id} is served by a PermissionDataProviderBase");
        Assert.Equal(new[] { 2000000005, 2000000251, 2000000254 }, bcTableIds);

        // The neighbouring permission tables have their own providers and their own arms.
        foreach (var id in new[] { 2000000004, 2000000250, 2000000167, 2000000165, 2000000166 })
            Assert.False(RecordPatches.IsPermissionFamilyTableId(id), $"{id} is not a family table");
    }

    [Fact]
    public void PlantPermissionSetupMonitor_BuildsBcsOwnMonitorOnTheDatabase_AndKeepsIt()
    {
        var tDatabase = typeof(NCLMetaTable).Assembly.GetType(Rt + "NavDatabase")!;
        var database = RuntimeHelpers.GetUninitializedObject(tDatabase);
        var pMonitor = tDatabase.GetProperty("PermissionSetupMonitor",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Null(pMonitor.GetValue(database));

        var planted = RecordPatches.PlantPermissionSetupMonitor(database);

        var monitor = pMonitor.GetValue(database);
        Assert.Same(planted, monitor);
        Assert.NotNull(monitor);
        Assert.Equal(Rt + "PermissionSetupMonitor", monitor!.GetType().FullName);
        // Built by BC's own ctor: its base TableChangeMonitor holds the database it watches,
        // and its version starts where BC's does.
        var fDb = monitor.GetType().BaseType!.GetField("database", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Same(database, fDb.GetValue(monitor));
        Assert.Equal(0, (int)monitor.GetType().GetProperty("SetupVersion")!.GetValue(monitor)!);

        // A second plant keeps the monitor BC's memo already keyed on.
        Assert.Same(monitor, RecordPatches.PlantPermissionSetupMonitor(database));
        Assert.Same(monitor, pMonitor.GetValue(database));
    }

    private static TableChangeMonitor NewPlantedMonitor()
    {
        var tDatabase = typeof(NCLMetaTable).Assembly.GetType(Rt + "NavDatabase")!;
        return (TableChangeMonitor)RecordPatches.PlantPermissionSetupMonitor(
            RuntimeHelpers.GetUninitializedObject(tDatabase))!;
    }

    [Theory]
    [InlineData(2000000165)] // Tenant Permission Set
    [InlineData(2000000166)] // Tenant Permission
    [InlineData(2000000053)] // Access Control
    public void TransactionEnd_AfterAWriteToAMonitoredTable_AdvancesBcsSetupVersionOnce(int tableId)
    {
        var monitor = NewPlantedMonitor();
        var changed = new System.Collections.Generic.HashSet<int>();

        RecordPatches.NotePermissionSetupTableWrite(monitor, changed, tableId);
        RecordPatches.NotePermissionSetupTableWrite(monitor, changed, tableId);
        Assert.Equal(0, monitor.SetupVersion); // a write alone does not move it — 67945's memo

        Assert.True(RecordPatches.EndPermissionSetupTransaction(monitor, changed));
        Assert.Equal(1, monitor.SetupVersion);

        // The set is BC's per-transaction one: the next transaction end, with no write, is quiet.
        Assert.False(RecordPatches.EndPermissionSetupTransaction(monitor, changed));
        Assert.Equal(1, monitor.SetupVersion);
    }

    [Theory]
    [InlineData(2000000068)] // Record Link: a system table the permission monitor does not watch
    [InlineData(60000)]
    public void TransactionEnd_AfterAWriteToAnUnmonitoredTable_LeavesSetupVersion(int tableId)
    {
        var monitor = NewPlantedMonitor();
        var changed = new System.Collections.Generic.HashSet<int>();

        RecordPatches.NotePermissionSetupTableWrite(monitor, changed, tableId);

        Assert.False(RecordPatches.EndPermissionSetupTransaction(monitor, changed));
        Assert.Equal(0, monitor.SetupVersion);
    }

    [Fact]
    public void TheMonitoredTables_AreReadFromBcsOwnMonitor()
    {
        // Routing reads PermissionSetupMonitor.TableIds rather than a runner copy of the list.
        var monitor = NewPlantedMonitor();
        Assert.Contains(2000000166, monitor.TableIds);
        Assert.DoesNotContain(2000000068, monitor.TableIds);
    }
}
