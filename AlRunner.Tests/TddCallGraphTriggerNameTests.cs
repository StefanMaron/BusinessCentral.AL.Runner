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

    /// <summary>
    /// A lookup, a drill-down and an assist-edit run from a page control, never from a record operation: the key a
    /// control's Lookup, Drilldown or AssistEdit raises on its field's table starts them (#5309), and no operation does.
    /// </summary>
    [Theory]
    [InlineData("OnLookup", "OnLookup")]
    [InlineData("OnAfterLookup", "OnLookup")]
    [InlineData("OnDrillDown", "OnDrillDown")]
    [InlineData("OnAssistEdit", "OnAssistEdit")]
    public void PageStartedName_IsStartedByTheControlCallOnly(string trigger, string started)
        => Assert.Equal(new[] { started }, TddCallGraph.StartedBy(trigger));

    /// <summary>The control for the fallback: a name nobody listed over-annotates, and misses nothing.</summary>
    [Theory]
    [InlineData("OnAfterSomethingNew")]
    [InlineData("OnPreInsertCheck")]
    public void UnknownName_IsStartedByEveryOperation(string trigger)
        => Assert.Equal(new[] { "OnInsert", "OnModify", "OnDelete", "OnRename", "OnValidate" }, TddCallGraph.StartedBy(trigger));
}
