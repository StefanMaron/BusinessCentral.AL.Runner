// TransactionWorldPermissionSetupVersionTests — AlRunner#5022.
//
// A RUNNER-MECHANISM test. The BC-observable claim (a set composed inside a failed guarded
// Codeunit.Run is recomposed on the next read outside it) is measured upstream by corpus codeunit
// 67947, ExpandedPermission_FailedGuardedRun_*. What this pins is the runner's wiring: every
// transaction world that ends WITHOUT committing advances the planted PermissionSetupMonitor's
// SetupVersion when it wrote a monitored table, as BC's SystemTableTriggers.OnTransactionEnded
// does on rollback.

using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class TransactionWorldPermissionSetupVersionTests
{
    private const int TenantPermissionTableId = 2000000166;
    private const int UnmonitoredTableId = 60000;

    private readonly BcEngineFixture _engine;

    public TransactionWorldPermissionSetupVersionTests(BcEngineFixture engine) => _engine = engine;

    private TableChangeMonitor PlantedMonitor()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.ExitNoTransactionScope();
        var monitor = typeof(RecordPatches)
            .GetField("_plantedPermissionSetupMonitor", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as TableChangeMonitor;
        Assert.True(monitor != null, "the engine did not plant a PermissionSetupMonitor");
        // Start from a clean per-transaction set, so an earlier test's write cannot bump here.
        RecordPatches.MarkCommitPoint();
        return monitor!;
    }

    public static TheoryData<string> RollbackWorlds => new() { "guarded-run", "bc-world" };

    private static void Begin(string world)
    {
        if (world == "guarded-run") ALDatabasePatches.BeginGuardedRunTransaction();
        else ALDatabasePatches.NoteBcBeginTransactionWorld();
    }

    private static void EndWithoutCommit(string world)
    {
        if (world == "guarded-run") ALDatabasePatches.EndGuardedRunTransaction(commit: false);
        else ALDatabasePatches.NoteBcEndTransactionWorld(null, commit: false);
    }

    [SkippableTheory]
    [MemberData(nameof(RollbackWorlds))]
    public void WorldEndedWithoutCommit_AfterAMonitoredWrite_AdvancesSetupVersion(string world)
    {
        var monitor = PlantedMonitor();
        var before = monitor.SetupVersion;

        Begin(world);
        RecordPatches.NotePermissionSetupTableWrite(TenantPermissionTableId);
        Assert.Equal(before, monitor.SetupVersion); // a write alone does not move it (corpus 67945)
        EndWithoutCommit(world);

        Assert.Equal(before + 1, monitor.SetupVersion);

        // The world's end consumed the written set: the next transaction end, with no write, is quiet.
        RecordPatches.MarkCommitPoint();
        Assert.Equal(before + 1, monitor.SetupVersion);
    }

    [SkippableTheory]
    [MemberData(nameof(RollbackWorlds))]
    public void WorldEndedWithoutCommit_AfterAnUnmonitoredWrite_LeavesSetupVersion(string world)
    {
        var monitor = PlantedMonitor();
        var before = monitor.SetupVersion;

        Begin(world);
        RecordPatches.NotePermissionSetupTableWrite(UnmonitoredTableId);
        EndWithoutCommit(world);

        Assert.Equal(before, monitor.SetupVersion);
    }
}
