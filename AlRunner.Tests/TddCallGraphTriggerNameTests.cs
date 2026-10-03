// The operation triggers a table or table extension trigger name is started by (#5286): a name no record operation
// is known to start is read as started by every operation, never as started by none. In-process: a pure mapping.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddCallGraphTriggerNameTests
{
    [Theory]
    [InlineData("OnInsert", "OnInsert")]
    [InlineData("OnBeforeInsert", "OnInsert")]
    [InlineData("OnAfterInsert", "OnInsert")]
    [InlineData("onafterModify", "OnModify")]
    [InlineData("OnBeforeDelete", "OnDelete")]
    [InlineData("OnAfterRename", "OnRename")]
    [InlineData("OnBeforeValidate", "OnValidate")]
    [InlineData("OnAfterValidate", "OnValidate")]
    [InlineData("OnValidate", "OnValidate")]
    public void KnownName_IsStartedByItsOperationOnly(string trigger, string operation)
        => Assert.Equal(new[] { operation }, TddCallGraph.StartedBy(trigger));

    /// <summary>A lookup, a drill-down and an assist-edit run from a page, never from a record operation.</summary>
    [Theory]
    [InlineData("OnLookup")]
    [InlineData("OnAfterLookup")]
    [InlineData("OnDrillDown")]
    [InlineData("OnAssistEdit")]
    public void PageOnlyName_IsStartedByNoOperation(string trigger)
        => Assert.Empty(TddCallGraph.StartedBy(trigger));

    /// <summary>The control for the fallback: a name nobody listed over-annotates, and misses nothing.</summary>
    [Theory]
    [InlineData("OnAfterSomethingNew")]
    [InlineData("OnPreInsertCheck")]
    public void UnknownName_IsStartedByEveryOperation(string trigger)
        => Assert.Equal(new[] { "OnInsert", "OnModify", "OnDelete", "OnRename", "OnValidate" }, TddCallGraph.StartedBy(trigger));
}
