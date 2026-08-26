// Issue #2051 — follow-on to #2043 (AlValueCaptureErrorVisibilityTests.cs). #2043 fixed
// the --capture-values path (AlValueCapture.CaptureField / execute's `capturedValues`):
// a field read that throws and a value whose ToString() throws are both reported with a
// distinct CaptureError instead of being flattened to the same `null` a genuinely-null AL
// variable produces. AlScopeInspector.ReadLocals (--dap's live "variables" request, #1642)
// already handled the read-throws case distinctly (Readable:false, "<unreadable: ...>"),
// but still called AlValueWireFormat.ToWireValue(object?) — the ONE-ARGUMENT overload — so
// a ToString() throw on an otherwise-successfully-read field was silently flattened back
// down to Readable:true, Value:null: indistinguishable from a genuinely null AL local in
// the DAP Variables pane.
//
// These are pure C# unit tests against AlScopeInspector.ReadField, the field-level helper
// extracted (mirroring AlValueCapture.CaptureField) so both failure modes are testable via
// an injected Func<object?> without a real NavMethodScope.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlScopeInspectorErrorVisibilityTests
{
    [Fact]
    public void ReadField_ReadThrows_ReportsUnreadableMarker_NotReadable()
    {
        var local = AlScopeInspector.ReadField("Broken",
            () => throw new NotSupportedException("field cannot be read"));

        Assert.Equal("Broken", local.Name);
        Assert.False(local.Readable);
        Assert.Equal("<unreadable: NotSupportedException>", local.Value);
    }

    // The load-bearing negative-direction test for this issue: a field that reads fine
    // but whose ToString() throws must NOT collapse to the same (Readable:true, Value:null)
    // shape a genuinely-null AL local produces (the next test down). Before the fix this
    // test fails: ReadField (via the 1-arg ToWireValue overload) reports Readable:true,
    // Value:null here too.
    [Fact]
    public void ReadField_ToStringThrows_ReportsUnreadableMarker_DistinctFromGenuineNull()
    {
        var local = AlScopeInspector.ReadField("Bad", () => new ThrowingToString());

        Assert.False(local.Readable);
        Assert.NotNull(local.Value);
        Assert.Contains(nameof(InvalidOperationException), (string)local.Value!);
        Assert.Contains("ToString", (string)local.Value!);
    }

    [Fact]
    public void ReadField_GenuinelyNullValue_ReportsReadableWithNullValue()
    {
        var local = AlScopeInspector.ReadField("Rec", () => null);

        Assert.True(local.Readable);
        Assert.Null(local.Value);
    }

    [Fact]
    public void ReadField_SuccessfulRead_ReportsReadableWithValue()
    {
        var local = AlScopeInspector.ReadField("Counter", () => 42);

        Assert.True(local.Readable);
        Assert.Equal(42, local.Value);
    }

    [Fact]
    public void ReadField_ToStringThrows_NeverPropagates()
    {
        var ex = Record.Exception(() => AlScopeInspector.ReadField("X", () => new ThrowingToString()));
        Assert.Null(ex);
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }
}
