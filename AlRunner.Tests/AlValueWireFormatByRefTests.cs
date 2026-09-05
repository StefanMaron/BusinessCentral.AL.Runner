// Issue #2488: --capture-values (#1640) and --dap's live variable inspection (#1642)
// rendered an AL `var` (by-reference) parameter as the CLR type name of its wrapper —
// `Microsoft.Dynamics.Nav.Runtime.ByRef`1[System.Int32]` — instead of the value the
// wrapper points at, with `captureError` null so a consumer could not tell the type name
// apart from a real value.
//
// BC materialises a `var` parameter on the generated `*_Scope` class as
// `Microsoft.Dynamics.Nav.Runtime.ByRef<T>`, a getter/setter pair over the caller's slot.
// It declares no ToString() override (decompiled from Ncl.dll), so object.ToString() runs
// and yields the type name — which is exactly what AlValueWireFormat's default arm took.
// The wrapper does implement `IByRef` with a non-generic `ObjectValue` accessor, so the
// inner value is reachable without reflection and without reimplementing anything BC owns
// (.claude/rules/precompiled-dll-respect.md).
//
// These are pure C# unit tests: `ByRef<T>` is constructible directly (AlRunner.Tests
// already references Ncl.dll), so a getter over a plain local reproduces the exact shape a
// `var` parameter has at runtime. Nothing here is a claim about Business Central's
// behaviour — the wire format of --capture-values is a runner surface — so this does not
// belong in the upstream corpus (.claude/rules/bc-behavior-tests-go-upstream.md).
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlValueWireFormatByRefTests
{
    // --- the reported bug: a by-ref parameter must render its VALUE ---------------------

    [Fact]
    public void ToWireValue_ByRefInteger_RendersInnerNumber_NotWrapperTypeName()
    {
        int slot = 5;
        var value = AlValueWireFormat.ToWireValue(
            new ByRef<int>(() => slot, v => slot = v), out var captureError);

        // A real JSON number, not a string: the AL local is an Integer and an Integer
        // is a CLR int, so the wire value must stay one (same contract as a by-value
        // Integer local, asserted below).
        Assert.Equal(5, value);
        Assert.IsType<int>(value);
        Assert.Null(captureError);
    }

    [Fact]
    public void ToWireValue_ByRefNavText_RendersInnerText_NotWrapperTypeName()
    {
        var slot = new NavText("y");
        var value = AlValueWireFormat.ToWireValue(
            new ByRef<NavText>(() => slot, v => slot = v), out var captureError);

        Assert.Equal("y", value);
        Assert.Null(captureError);
    }

    [Fact]
    public void ToWireValue_ByRef_ReadsThroughToTheCurrentSlotValue_NotACopy()
    {
        int slot = 5;
        var byRef = new ByRef<int>(() => slot, v => slot = v);
        Assert.Equal(5, AlValueWireFormat.ToWireValue(byRef));

        slot = 6; // what `v := v + 1` inside the callee does to the caller's local
        Assert.Equal(6, AlValueWireFormat.ToWireValue(byRef));
    }

    // --- the other direction: by-value locals must be untouched ------------------------
    // A fix that unwrapped indiscriminately, or that stringified everything, fails here.

    [Fact]
    public void ToWireValue_PlainInteger_StillRendersAsNumber()
    {
        var value = AlValueWireFormat.ToWireValue(6, out var captureError);
        Assert.Equal(6, value);
        Assert.IsType<int>(value);
        Assert.Null(captureError);
    }

    [Fact]
    public void ToWireValue_PlainNavText_StillRendersViaItsOwnToString()
    {
        var value = AlValueWireFormat.ToWireValue(new NavText("x"), out var captureError);
        Assert.Equal("x", value);
        Assert.Null(captureError);
    }

    // --- an unreadable wrapper still surfaces a captureError, never a wrapper name -----

    [Fact]
    public void ToWireValue_ByRefWhoseGetterThrows_ReportsCaptureErrorNamingExceptionType()
    {
        var value = AlValueWireFormat.ToWireValue(
            new ByRef<int>(() => throw new InvalidOperationException("slot is gone"), _ => { }),
            out var captureError);

        Assert.Null(value);
        Assert.NotNull(captureError);
        Assert.Contains(nameof(InvalidOperationException), captureError);
    }

    [Fact]
    public void ToWireValue_ByRefWithNoGetterInstalled_ReportsCaptureError_NotWrapperName()
    {
        // ByRef<T>'s parameterless ctor leaves `getter` null, so ObjectValue NREs.
        var value = AlValueWireFormat.ToWireValue(new ByRef<int>(), out var captureError);

        Assert.Null(value);
        Assert.NotNull(captureError);
        Assert.Contains(nameof(NullReferenceException), captureError);
    }

    [Fact]
    public void ToWireValue_ByRefOverNullInner_IsANullValueWithNoCaptureError()
    {
        // A `var` parameter of a reference-typed AL value that is genuinely null must be
        // indistinguishable from a genuinely null by-value local — and distinguishable
        // from an unreadable one (issue #2043's contract, preserved through the unwrap).
        var value = AlValueWireFormat.ToWireValue(
            new ByRef<NavText>(() => null!, _ => { }), out var captureError);

        Assert.Null(value);
        Assert.Null(captureError);
    }

    // --- the unwrap must terminate ------------------------------------------------------

    [Fact]
    public void ToWireValue_NestedByRef_UnwrapsToTheInnermostValue()
    {
        int slot = 7;
        var inner = new ByRef<int>(() => slot, v => slot = v);
        var outer = new ByRef<ByRef<int>>(() => inner, v => inner = v);

        Assert.Equal(7, AlValueWireFormat.ToWireValue(outer, out var captureError));
        Assert.Null(captureError);
    }

    [Fact]
    public void ToWireValue_SelfReferentialByRef_TerminatesWithCaptureError()
    {
        ByRef<object>? self = null;
        self = new ByRef<object>(() => self!, _ => { });

        var value = AlValueWireFormat.ToWireValue(self, out var captureError);

        Assert.Null(value);
        Assert.NotNull(captureError);
    }

    // --- both consumers of the shared renderer -----------------------------------------

    [Fact]
    public void CaptureField_ByRefInteger_CapturesInnerNumber_NotWrapperTypeName()
    {
        int slot = 5;
        var byRef = new ByRef<int>(() => slot, v => slot = v);
        var captured = AlValueCapture.CaptureField("Bump", "v", statementId: 1, readField: () => byRef);

        Assert.Equal(5, captured.Value);
        Assert.Null(captured.CaptureError);
    }

    [Fact]
    public void ScopeInspector_ReadField_ByRefInteger_ReportsInnerNumberAsReadable()
    {
        int slot = 5;
        var byRef = new ByRef<int>(() => slot, v => slot = v);
        var local = AlScopeInspector.ReadField("v", () => byRef);

        Assert.Equal("v", local.Name);
        Assert.Equal(5, local.Value);
        Assert.True(local.Readable);
    }

    // --- the symptom that was actually reported ----------------------------------------
    // A `var grand: Integer` accumulator threaded through a per-iteration procedure was
    // reported as the wrapper name in EVERY iteration. Two failures in one: each record
    // rendered the type name, and — because that name never changes — DiffAndUpdate saw
    // no change and suppressed every observation after the first. Both must be gone.

    [Fact]
    public void DiffAndUpdate_ByRefAccumulator_EmitsEachIterationsRealValue()
    {
        int slot = 5;
        var byRef = new ByRef<int>(() => slot, v => slot = v);
        var fields = new (string Name, Func<object?> ReadField)[] { ("grand", () => byRef) };
        var lastKnown = new Dictionary<string, (object? Value, string? Error)>();

        var first = AlValueCapture.DiffAndUpdate("Subtotal", 1, fields, lastKnown, isBaseline: false);
        Assert.Equal(5, Assert.Single(first).Value);

        slot = 6; // the callee's `grand := grand + 1`
        var second = AlValueCapture.DiffAndUpdate("Subtotal", 2, fields, lastKnown, isBaseline: false);
        Assert.Equal(6, Assert.Single(second).Value);

        // Unchanged between observations is still suppressed — the unwrap must not turn
        // every by-ref local into a record on every statement.
        var third = AlValueCapture.DiffAndUpdate("Subtotal", 3, fields, lastKnown, isBaseline: false);
        Assert.Empty(third);
    }
}
