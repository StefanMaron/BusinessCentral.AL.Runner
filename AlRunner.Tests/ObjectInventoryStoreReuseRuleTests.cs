// ObjectInventoryStoreReuseRuleTests — issue #4859. A filled AllObj / AllObjWithCaption store is
// handed to the next test codeunit only when a fresh store would end up with the very same rows.
// The AL-level proof is AllObjPopulateCostTests (reuse happens, and a written store is never
// reused); this pins each clause of the rule, including the one no platform-only fixture can
// reach: a store widened by another app's call stack must not answer a narrower read.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ObjectInventoryStoreReuseRuleTests
{
    private static readonly RecordPatches.ObjectInventoryStamp Stamp = default;
    private static readonly RecordPatches.ObjectInventoryStamp NewerStamp = default(RecordPatches.ObjectInventoryStamp) with { Tables = 1 };
    private static readonly Guid AppGroup = new("6a4859e0-0000-4c38-9e25-000000000001");

    private static RecordPatches.ObjectInventoryKey Key(string visibility, RecordPatches.ObjectInventoryStamp? stamp = null)
        => new(stamp ?? Stamp, AppGroup, visibility);

    private static readonly RecordPatches.ObjectInventoryKey Narrow = Key("A,B");
    private static readonly RecordPatches.ObjectInventoryKey Wide = Key("A,B,TR");

    private static bool Reusable(bool written, RecordPatches.ObjectInventoryKey[] applied, int storedRows,
        RecordPatches.ObjectInventoryKey current, int currentRows, bool sameMetaTable = true)
        => RecordPatches.IsReusableObjectInventoryStore(written, applied, storedRows, current, currentRows, sameMetaTable);

    [Fact]
    public void AStoreFilledUnderTheSameKey_AndNothingElse_IsReused()
        => Assert.True(Reusable(false, new[] { Narrow }, 300, Narrow, 300));

    [Fact]
    public void AStoreAlWroteTo_IsNotReused()
        => Assert.False(Reusable(true, new[] { Narrow }, 300, Narrow, 300));

    /// <summary>Filled under the narrow key and then widened by another app on the call stack: it
    /// holds rows the narrow read must not see, so its count is larger and it is refused.</summary>
    [Fact]
    public void AStoreWidenedBeyondTheCurrentKey_IsNotReused()
        => Assert.False(Reusable(false, new[] { Narrow, Wide }, 320, Narrow, 300));

    /// <summary>Widening that added no row leaves a store identical to a fresh narrow one.</summary>
    [Fact]
    public void AStoreWidenedWithoutAddingARow_IsReused()
        => Assert.True(Reusable(false, new[] { Narrow, Wide }, 300, Narrow, 300));

    /// <summary>Filled only under the wide key: a fresh store on a narrow first read would hold
    /// fewer rows, so the wide one is refused even for the narrow key it never took.</summary>
    [Fact]
    public void AStoreThatNeverTookTheCurrentKey_IsNotReused()
        => Assert.False(Reusable(false, new[] { Wide }, 300, Narrow, 300));

    /// <summary>A row from an older stamp may carry an owner that has since become known.</summary>
    [Fact]
    public void AStoreFilledPartlyUnderAnOlderStamp_IsNotReused()
    {
        var current = Key("A,B", NewerStamp);
        Assert.False(Reusable(false, new[] { Narrow, current }, 300, current, 300));
    }

    [Fact]
    public void AStoreLaidOutByAnotherMetaTable_IsNotReused()
        => Assert.False(Reusable(false, new[] { Narrow }, 300, Narrow, 300, sameMetaTable: false));
}
