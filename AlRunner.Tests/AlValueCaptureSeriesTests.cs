// Issue #2074: --capture-values used to record ONE end-of-test snapshot per AL local
// (AlValueCapture.OnExit walked every [NavName] field once, unconditionally). A local
// reassigned inside a loop that runs N times therefore collapsed to its FINAL value —
// ALchemist's inline loop rendering needs the full per-iteration series (e.g. `myInt = 2
// .. 56 (x10)`), which a single snapshot cannot supply.
//
// These are pure C# unit tests against AlValueCapture.DiffAndUpdate — the diff engine
// behind the new per-execution series, extracted (same reasoning as CaptureField's own
// extraction for #2043) so it is testable with injected read delegates instead of a real
// NavMethodScope. No BC artifact needed; this is the runner's own mechanism, not AL/BC
// behaviour, so it belongs here rather than upstream (.claude/rules/
// bc-behavior-tests-go-upstream.md) — the end-to-end wire proof against a real compiled
// AL loop lives in ServerExecuteCapturedValuesSeriesTests (needs the BC artifact cache).
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlValueCaptureSeriesTests
{
    private static (string Name, Func<object?> ReadField) Field(string name, object? value) =>
        (name, () => value);

    // --- The core claim: repeated executions of the SAME statement each emit a record ---

    [Fact]
    public void DiffAndUpdate_ValueChangesAcrossThreeObservations_EmitsThreeRecordsInOrder()
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();

        // Observation 1 (baseline: the scope's very first StmtHit, nothing has run yet).
        var r0 = AlValueCapture.DiffAndUpdate("OnRun", -1,
            new[] { Field("Sum", 0) }, lastKnown, isBaseline: true);
        Assert.Empty(r0); // baseline never emits — nothing produced this value

        // Iteration 1: Sum := Sum + 1 (statement 5 just ran).
        var r1 = AlValueCapture.DiffAndUpdate("OnRun", 5,
            new[] { Field("Sum", 1) }, lastKnown, isBaseline: false);
        // Iteration 2: same statement runs again, Sum := Sum + 2.
        var r2 = AlValueCapture.DiffAndUpdate("OnRun", 5,
            new[] { Field("Sum", 3) }, lastKnown, isBaseline: false);
        // Iteration 3: same statement a third time, Sum := Sum + 3.
        var r3 = AlValueCapture.DiffAndUpdate("OnRun", 5,
            new[] { Field("Sum", 6) }, lastKnown, isBaseline: false);

        var series = r1.Concat(r2).Concat(r3).ToList();
        Assert.Equal(3, series.Count);
        Assert.Equal(new object?[] { 1, 3, 6 }, series.Select(v => v.Value).ToArray());
        // All three executions are the SAME source statement — same statementId for all.
        Assert.All(series, v => Assert.Equal(5, v.StatementId));
        Assert.All(series, v => Assert.Equal("OnRun", v.ScopeName));
        Assert.All(series, v => Assert.Equal("Sum", v.VariableName));
    }

    [Fact]
    public void DiffAndUpdate_ValueUnchangedBetweenObservations_EmitsNothing()
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();
        AlValueCapture.DiffAndUpdate("OnRun", -1, new[] { Field("X", 42) }, lastKnown, isBaseline: true);

        // Same value observed again — nothing was "produced" between these two points.
        var changed = AlValueCapture.DiffAndUpdate("OnRun", 2, new[] { Field("X", 42) }, lastKnown, isBaseline: false);
        Assert.Empty(changed);
    }

    // --- Attribution: the producing statement is the one BEFORE the current observation ---

    [Fact]
    public void DiffAndUpdate_AttributesChangeToThePreviousStatementId_NotTheCurrentOne()
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();
        AlValueCapture.DiffAndUpdate("OnRun", -1, new[] { Field("Counter", 0) }, lastKnown, isBaseline: true);

        // attributionStatementId=2 is passed in as "the statement that just finished" —
        // the caller (OnStmtHit) is responsible for passing the PREVIOUS statement's id,
        // never the one currently about to run. This test pins that the diff engine
        // records whatever id it's given, not scope.StatementNumber or the current one.
        var changed = AlValueCapture.DiffAndUpdate("OnRun", 2, new[] { Field("Counter", 42) }, lastKnown, isBaseline: false);
        var entry = Assert.Single(changed);
        Assert.Equal(2, entry.StatementId);
        Assert.Equal(42, entry.Value);
    }

    // --- Untouched fields: no execution ever produced a value, so no record ---

    [Fact]
    public void DiffAndUpdate_FieldNeverChangesFromBaseline_NeverEmitted()
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();
        AlValueCapture.DiffAndUpdate("OnRun", -1,
            new[] { Field("Touched", 0), Field("Untouched", 0) }, lastKnown, isBaseline: true);

        var changed = AlValueCapture.DiffAndUpdate("OnRun", 0,
            new[] { Field("Touched", 99), Field("Untouched", 0) }, lastKnown, isBaseline: false);

        var entry = Assert.Single(changed);
        Assert.Equal("Touched", entry.VariableName);
    }

    // --- Degenerate case: Exit() is the ONLY observation (no StmtHit fired at all) ---

    [Fact]
    public void DiffAndUpdate_NoPriorObservation_NonBaselineCall_EmitsEveryField()
    {
        // Mirrors OnExit's own fallback for a scope whose body never called StmtHit:
        // an empty lastKnown makes every field look "changed from nothing observed",
        // which is the pre-#2074 "walk everything unconditionally" behaviour for this
        // one degenerate shape.
        var lastKnown = new Dictionary<string, (object?, string?)>();
        var changed = AlValueCapture.DiffAndUpdate("OnRun", 0,
            new[] { Field("A", 1), Field("B", "x") }, lastKnown, isBaseline: false);

        Assert.Equal(2, changed.Count);
        Assert.Contains(changed, v => v.VariableName == "A" && Equals(v.Value, 1));
        Assert.Contains(changed, v => v.VariableName == "B" && Equals(v.Value, "x"));
    }

    // --- Read/ToString failures still surface through CaptureField, not swallowed ---

    [Fact]
    public void DiffAndUpdate_FieldReadThrows_StillEmittedWithCaptureError()
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();
        var fields = new (string, Func<object?>)[]
        {
            ("Broken", () => throw new InvalidOperationException("boom")),
        };

        var changed = AlValueCapture.DiffAndUpdate("OnRun", 0, fields, lastKnown, isBaseline: false);
        var entry = Assert.Single(changed);
        Assert.Null(entry.Value);
        Assert.NotNull(entry.CaptureError);
        Assert.Contains(nameof(InvalidOperationException), entry.CaptureError);
    }

    [Fact]
    public void DiffAndUpdate_TransitionFromErrorToRealValue_CountsAsAChange()
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();
        var throwing = new (string, Func<object?>)[] { ("Flaky", () => throw new InvalidOperationException()) };
        AlValueCapture.DiffAndUpdate("OnRun", -1, throwing, lastKnown, isBaseline: true);

        var recovered = new (string, Func<object?>)[] { ("Flaky", () => 7) };
        var changed = AlValueCapture.DiffAndUpdate("OnRun", 1, recovered, lastKnown, isBaseline: false);

        var entry = Assert.Single(changed);
        Assert.Equal(7, entry.Value);
        Assert.Null(entry.CaptureError);
    }
}
