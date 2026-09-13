// ReportRunTransactionWorldTests — AlRunner#2904.
//
// A RUNNER-MECHANISM test. The BC claim — a Report.Run that enters a transaction world is
// refused with a pending write and otherwise commits the report's writes — is measured by
// corpus codeunit 60040 "Test TxModel Report Run" (StefanMaron/BusinessCentral.AL.Language.Tests
// PR #344). This pins the runner's copy of BC's guard, because the runner replaces NavReport.Run
// outright (NavReportSync.SyncRun) and BC's own RunReportCoreAsync never runs here.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ReportRunTransactionWorldTests
{
    private const int UpdateNoLocks = 0;
    private const int Update = 1;
    private const int Browse = 3;

    private readonly BcEngineFixture _engine;

    public ReportRunTransactionWorldTests(BcEngineFixture engine) => _engine = engine;

    private void Arrange(int currentTransactionType)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        ALDatabasePatches.ResetWriteTransactionState();
        ALDatabasePatches.ALDatabase_SetCurrentTransactionType(currentTransactionType);
        Assert.Equal(currentTransactionType, ALDatabasePatches.ALDatabase_GetCurrentTransactionType());
    }

    [SkippableFact]
    public void RequestPage_EntersTheWorld_WhateverTheTransactionType()
    {
        Arrange(UpdateNoLocks);
        Assert.True(ALDatabasePatches.ReportRunEntersTransactionWorld(useRequestForm: true, UpdateNoLocks));
    }

    [SkippableFact]
    public void NoRequestPage_DefaultTransactionType_DoesNotEnterTheWorld()
    {
        Arrange(UpdateNoLocks);
        Assert.False(ALDatabasePatches.ReportRunEntersTransactionWorld(useRequestForm: false, UpdateNoLocks));
    }

    [SkippableFact]
    public void NoRequestPage_TransactionTypeDiffersFromTheSession_EntersTheWorld()
    {
        Arrange(UpdateNoLocks);
        Assert.True(ALDatabasePatches.ReportRunEntersTransactionWorld(useRequestForm: false, Update));
    }

    [SkippableFact]
    public void NoRequestPage_TransactionTypeEqualToTheSession_DoesNotEnterTheWorld()
    {
        Arrange(Browse);
        Assert.False(ALDatabasePatches.ReportRunEntersTransactionWorld(useRequestForm: false, Browse));
    }

    [SkippableFact]
    public void NoRequestPage_UpdateNoLocksReport_DoesNotEnterTheWorldEvenWhenTheSessionDiffers()
    {
        Arrange(Browse);
        Assert.False(ALDatabasePatches.ReportRunEntersTransactionWorld(useRequestForm: false, UpdateNoLocks));
    }
}
