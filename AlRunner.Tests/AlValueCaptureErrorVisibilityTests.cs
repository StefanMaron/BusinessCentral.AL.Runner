// Issue #2043: --capture-values (#1640) had two catch blocks in AlValueCapture.cs /
// AlValueWireFormat.cs that DROPPED information instead of reporting it —
//   1. a field whose FieldInfo.GetValue(scope) threw was silently `continue`d, so the
//      variable was simply absent from the captured list;
//   2. a raw value whose ToString() threw was flattened to `null`, indistinguishable
//      from an AL variable that is genuinely null.
// Both are the right instinct (a capture must never crash the run), but the consumer
// (the VS Code extension, reading `capturedValues` off `execute`'s wire response — see
// ServerProtocol.ToWire(AlCapturedValue)) could not tell "this variable does not exist",
// "this variable is null", and "reading this variable failed" apart. .claude/rules/
// loud-failures.md is the governing rule: a missing/faked value must never be silently
// indistinguishable from a real one.
//
// These are pure C# unit tests against AlValueCapture.CaptureField (the field-level
// helper extracted so this is testable without a real NavMethodScope — the reflective
// read is abstracted behind a `Func<object?>` so a throw can be injected directly) and
// AlValueWireFormat.ToWireValue's error-surfacing overload. No BC artifact needed.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlValueCaptureErrorVisibilityTests
{
    // --- AlValueWireFormat.ToWireValue(object?, out string?) ---------------------------

    [Fact]
    public void ToWireValue_GenuinelyNull_ReturnsNullValueAndNullCaptureError()
    {
        var value = AlValueWireFormat.ToWireValue(null, out var captureError);
        Assert.Null(value);
        Assert.Null(captureError);
    }

    [Fact]
    public void ToWireValue_ClrPrimitive_PassesThroughWithNoCaptureError()
    {
        var value = AlValueWireFormat.ToWireValue(42, out var captureError);
        Assert.Equal(42, value);
        Assert.Null(captureError);
    }

    [Fact]
    public void ToWireValue_ToStringThrows_ReturnsNullValueWithCaptureErrorNamingExceptionType()
    {
        var value = AlValueWireFormat.ToWireValue(new ThrowingToString(), out var captureError);
        Assert.Null(value);
        Assert.NotNull(captureError);
        // The exact exception type must be nameable from the marker — an unusual throw
        // on ToString() is worth being able to see (issue's "Include the exception type").
        Assert.Contains(nameof(InvalidOperationException), captureError);
    }

    [Fact]
    public void ToWireValue_NormalObject_UsesToStringWithNoCaptureError()
    {
        var value = AlValueWireFormat.ToWireValue(new NormalObject(), out var captureError);
        Assert.Equal("normal-value", value);
        Assert.Null(captureError);
    }

    // --- AlValueCapture.CaptureField (field-level helper behind OnExit) ----------------

    [Fact]
    public void CaptureField_ReadThrows_ReportsCaptureErrorAndNullValue_NotOmitted()
    {
        var captured = AlValueCapture.CaptureField("OnRun", "Broken", statementId: 3,
            readField: () => throw new NotSupportedException("field cannot be read"));

        Assert.Equal("OnRun", captured.ScopeName);
        Assert.Equal("Broken", captured.VariableName);
        Assert.Equal(3, captured.StatementId);
        Assert.Null(captured.Value);
        Assert.NotNull(captured.CaptureError);
        Assert.Contains(nameof(NotSupportedException), captured.CaptureError);
    }

    [Fact]
    public void CaptureField_ToStringThrows_ReportsCaptureError_DistinctFromGenuineNull()
    {
        var captured = AlValueCapture.CaptureField("OnRun", "Bad", statementId: 1,
            readField: () => new ThrowingToString());

        Assert.Null(captured.Value);
        Assert.NotNull(captured.CaptureError);
        Assert.Contains(nameof(InvalidOperationException), captured.CaptureError);
    }

    [Fact]
    public void CaptureField_GenuinelyNullValue_ReportsNullWithNoCaptureError()
    {
        var captured = AlValueCapture.CaptureField("OnRun", "Rec", statementId: 2,
            readField: () => null);

        Assert.Null(captured.Value);
        Assert.Null(captured.CaptureError);
    }

    [Fact]
    public void CaptureField_SuccessfulRead_ReportsValueWithNoCaptureError()
    {
        var captured = AlValueCapture.CaptureField("OnRun", "Counter", statementId: 0,
            readField: () => 42);

        Assert.Equal(42, captured.Value);
        Assert.Null(captured.CaptureError);
    }

    // Acceptance criterion #4 ("Neither path can throw out of OnExit"): CaptureField is
    // the piece OnExit calls per field, with no try/catch of its own around the call —
    // so if either internal catch (read-throws, ToString-throws) ever let an exception
    // escape, it would propagate straight out of OnExit and crash the AL run mid-scope-
    // teardown. Assert.Null(Record.Exception(...)) makes that an explicit, named claim
    // rather than an implicit one (the four tests above only reached their assertions
    // because nothing threw first — this test is the one that says so on purpose).
    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(FieldAccessException))]
    public void CaptureField_ReadThrows_NeverPropagates_ForAnyExceptionType(Type exceptionType)
    {
        var ex = Record.Exception(() => AlValueCapture.CaptureField("OnRun", "X", 0,
            readField: () => throw (Exception)Activator.CreateInstance(exceptionType)!));
        Assert.Null(ex);
    }

    [Fact]
    public void CaptureField_ToStringThrows_NeverPropagates()
    {
        var ex = Record.Exception(() => AlValueCapture.CaptureField("OnRun", "X", 0,
            readField: () => new ThrowingToString()));
        Assert.Null(ex);
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    private sealed class NormalObject
    {
        public override string ToString() => "normal-value";
    }
}
