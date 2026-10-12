// StorageLayerFieldWriteGateTests — issue #5509.
//
// RUNNER-MECHANISM test. The AutoIncrement key and the system stamps are written by a prepend on
// RecordImplementation.InsertRecordAsync / ModifyRecordAsync, but only for a record that has
// entered NavRecord.InsertAsync(4) / ModifyAsync(4) (BcRuntime.NoteInsertFunnel / NoteModifyFunnel).
// NavForm.SyncTempTableWithSourceTableAsync calls InsertRecordAsync / ModifyRecordAsync directly on
// a page's temp copy of a row that already carries its source's stamps, and must not be written to
// (decompiled on Ncl 28.4.53241.54346). Neither the corpus nor tests/runner-extras reaches that
// route, so this pins the gate itself: a mark is consumed once, per record, per direction.
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class StorageLayerFieldWriteGateTests
{
    private readonly BcEngineFixture _engine;

    public StorageLayerFieldWriteGateTests(BcEngineFixture engine) => _engine = engine;

    private static NavRecord NewRecord()
        => (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(NavRecord));

    private void RequireEngine()
        => TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    [SkippableFact]
    public void InsertMark_IsConsumedOnce_AndOnlyForTheRecordThatNotedIt()
    {
        RequireEngine();
        var noted = NewRecord();
        var other = NewRecord();

        Assert.False(BcRuntime.TryConsumeInsertFunnelMark(noted), "nothing noted yet: a direct InsertRecordAsync call must not write");

        BcRuntime.NoteInsertFunnel(noted);
        Assert.False(BcRuntime.TryConsumeInsertFunnelMark(other), "another record's mark is not this record's");
        Assert.True(BcRuntime.TryConsumeInsertFunnelMark(noted), "the funnel noted this record, so the storage write may write");
        Assert.False(BcRuntime.TryConsumeInsertFunnelMark(noted), "one note buys one write");
    }

    [SkippableFact]
    public void ModifyMark_IsConsumedOnce_AndOnlyForTheRecordThatNotedIt()
    {
        RequireEngine();
        var noted = NewRecord();
        var other = NewRecord();

        Assert.False(BcRuntime.TryConsumeModifyFunnelMark(noted));

        BcRuntime.NoteModifyFunnel(noted);
        Assert.False(BcRuntime.TryConsumeModifyFunnelMark(other));
        Assert.True(BcRuntime.TryConsumeModifyFunnelMark(noted));
        Assert.False(BcRuntime.TryConsumeModifyFunnelMark(noted));
    }

    [SkippableFact]
    public void TheTwoDirectionsKeepSeparateMarks()
    {
        // An insert note must not authorise a modify write (that would stamp SystemModifiedAt on
        // the way in) and a modify note must not authorise an insert write (that would reset
        // SystemCreatedAt on every modify).
        RequireEngine();
        var record = NewRecord();

        BcRuntime.NoteInsertFunnel(record);
        Assert.False(BcRuntime.TryConsumeModifyFunnelMark(record));
        Assert.True(BcRuntime.TryConsumeInsertFunnelMark(record));

        BcRuntime.NoteModifyFunnel(record);
        Assert.False(BcRuntime.TryConsumeInsertFunnelMark(record));
        Assert.True(BcRuntime.TryConsumeModifyFunnelMark(record));
    }

    [SkippableFact]
    public void ANoteIsRepeatableWithoutAccumulating()
    {
        // A funnel call that threw before reaching the storage write leaves its mark behind; the
        // retry notes again. That must still authorise exactly one write.
        RequireEngine();
        var record = NewRecord();

        BcRuntime.NoteInsertFunnel(record);
        BcRuntime.NoteInsertFunnel(record);
        Assert.True(BcRuntime.TryConsumeInsertFunnelMark(record));
        Assert.False(BcRuntime.TryConsumeInsertFunnelMark(record));
    }

    [SkippableFact]
    public void TheStorageWriters_AreNoOps_ForAnUnnotedOrNullRecord()
    {
        RequireEngine();
        BcRuntime.WriteStorageFieldsOnInsert(null!);
        BcRuntime.WriteStorageFieldsOnModify(null!);
        BcRuntime.WriteStorageFieldsOnInsert(NewRecord());
        BcRuntime.WriteStorageFieldsOnModify(NewRecord());
    }
}
