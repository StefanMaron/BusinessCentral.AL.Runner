// Issue #2524 — when the managed find bypass for the virtual Field table (2000000041) may fire.
//
// The bypass exists because BC's native InnerFindAsync prologue AVs on a find whose
// MetaApplicationObject is the 2000000041 metatable, and it serves the table's REAL field
// metadata rows. Applying it to an AL `Record "Field" temporary` was wrong twice over: the
// temporary table's own rows were hidden behind the metadata rows, and the bypass's populate
// step wrote those metadata rows INTO the AL programmer's temporary table. Base Application
// report 8621 "Config. Package - Process" keys its transformation rules off
// `Format(TempField."No.")` and read back '0' for every one of them.
//
// The BC-behaviour claim underneath ("a temporary Record \"Field\" round-trips what AL wrote,
// and the non-temporary half still reports real metadata") is not pinned here — it belongs
// upstream, and is StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60663. What is pinned
// here is the runner's own gate rule, which has no BC counterpart because BC has no bypass.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class FieldFindBypassTemporaryGateTests
{
    private const int FieldTableId = 2000000041;

    [Fact]
    public void FieldTable_DatabaseBacked_TakesTheBypass()
    {
        Assert.True(RecordPatches.ShouldTakeFieldFindBypass(FieldTableId, isDatabaseBacked: true));
    }

    [Fact]
    public void FieldTable_TemporaryRecord_DoesNotTakeTheBypass()
    {
        // The whole of #2524. A temporary record's DataAccess is never marked database-backed
        // (NavDataAccessSource_GetDataAccessForTable marks only when isTemporary is false), so
        // this is the case that must fall through to BC's own find.
        Assert.False(RecordPatches.ShouldTakeFieldFindBypass(FieldTableId, isDatabaseBacked: false));
    }

    [Theory]
    [InlineData(2000000038)] // AllObj
    [InlineData(2000000136)] // Table Metadata
    [InlineData(2000000026)] // Integer
    [InlineData(18)]         // an ordinary application table
    [InlineData(0)]
    [InlineData(-1)]         // FindRequestTableId's "could not read" answer
    public void AnyOtherTable_NeverTakesTheBypass_WhicheverBackingItHas(int tableId)
    {
        // The other conjunct, and it is not academic: dropping it would route every find in the
        // run through a bypass built for one table's rows.
        Assert.False(RecordPatches.ShouldTakeFieldFindBypass(tableId, isDatabaseBacked: true));
        Assert.False(RecordPatches.ShouldTakeFieldFindBypass(tableId, isDatabaseBacked: false));
    }

    /// <summary>
    /// The marker the gate reads. An unmarked provider — which is what a temporary record's
    /// DataAccess carries — must answer false, and null must not throw: the gate calls this on
    /// every Field find and a throw there would take down an ordinary metadata read.
    /// </summary>
    [Fact]
    public void UnmarkedProvider_IsNotDatabaseBacked()
    {
        var provider = new object();
        Assert.False(BlobStoreIsolationPatches.IsDatabaseBacked(provider));
        Assert.False(BlobStoreIsolationPatches.IsDatabaseBacked(null));
    }

    [Fact]
    public void MarkedProvider_IsDatabaseBacked_AndTheMarkIsPerProvider()
    {
        var marked = new FakeDataAccess();
        var unmarked = new FakeDataAccess();
        BlobStoreIsolationPatches.MarkDatabaseBacked(marked);

        Assert.True(BlobStoreIsolationPatches.IsDatabaseBacked(marked.DataProvider));
        Assert.False(BlobStoreIsolationPatches.IsDatabaseBacked(unmarked.DataProvider));
    }

    /// <summary>
    /// Stands in for BC's DataAccess: MarkDatabaseBacked reads a `DataProvider` property off it
    /// and marks the PROVIDER, not the DataAccess, which is the object the gate then asks about.
    /// </summary>
    private sealed class FakeDataAccess
    {
        public object DataProvider { get; } = new();
    }
}
