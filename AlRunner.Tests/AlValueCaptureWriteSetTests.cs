// #2056 full-fidelity captures: DiffAndUpdate's `assigned` parameter (a record per
// execution of an assigning statement, changed or not) and AlScopeFrames (per-scope
// diff state). No BC artifact needed; the wire proof is in the ServerExecute* classes.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlValueCaptureWriteSetTests
{
    private static (string Name, Func<object?> ReadField) Field(string name, object? value) =>
        (name, () => value);

    private static Dictionary<string, (object?, string?)> Primed(params (string Name, object? Value)[] fields)
    {
        var lastKnown = new Dictionary<string, (object?, string?)>();
        AlValueCapture.DiffAndUpdate("OnRun", 0, fields.Select(f => Field(f.Name, f.Value)), lastKnown, isBaseline: true);
        return lastKnown;
    }

    [Fact]
    public void DiffAndUpdate_AssignedButUnchanged_IsEmittedWithTheCurrentValue()
    {
        // x := 5; x := 5;   -> the second execution is a record too.
        var lastKnown = Primed(("x", 0));
        AlValueCapture.DiffAndUpdate("OnRun", 0, new[] { Field("x", 5) }, lastKnown, isBaseline: false);

        var again = AlValueCapture.DiffAndUpdate("OnRun", 1, new[] { Field("x", 5) }, lastKnown,
            isBaseline: false, assigned: new HashSet<string> { "x" });

        var entry = Assert.Single(again);
        Assert.Equal("x", entry.VariableName);
        Assert.Equal(5, entry.Value);
        Assert.Equal(1, entry.StatementId); // attributed to the statement that assigned it
    }

    [Fact]
    public void DiffAndUpdate_UnchangedAndNotAssigned_StillNothing()
    {
        var lastKnown = Primed(("x", 5), ("y", 7));
        var r = AlValueCapture.DiffAndUpdate("OnRun", 3, new[] { Field("x", 5), Field("y", 7) }, lastKnown,
            isBaseline: false, assigned: new HashSet<string> { "y" });

        var entry = Assert.Single(r);
        Assert.Equal("y", entry.VariableName); // y was assigned (to the same value); x was not touched
    }

    [Fact]
    public void DiffAndUpdate_ChangedAndAssigned_IsOneRecord_NotTwo()
    {
        var lastKnown = Primed(("x", 1));
        var r = AlValueCapture.DiffAndUpdate("OnRun", 2, new[] { Field("x", 2) }, lastKnown,
            isBaseline: false, assigned: new HashSet<string> { "x" });
        var entry = Assert.Single(r);
        Assert.Equal(2, entry.Value);
    }

    [Fact]
    public void DiffAndUpdate_AssignedName_IsMatchedCaseInsensitively_LikeAL()
    {
        var lastKnown = Primed(("Total", 5));
        var r = AlValueCapture.DiffAndUpdate("OnRun", 2, new[] { Field("Total", 5) }, lastKnown,
            isBaseline: false, assigned: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "total" });
        Assert.Single(r);
    }

    [Fact]
    public void DiffAndUpdate_AssignedRecordDoesNotDisturbChangeDetection_Afterwards()
    {
        // An unchanged-but-assigned record must not make the NEXT observation think the
        // value changed (lastKnown stays the same value it already was).
        var lastKnown = Primed(("x", 5));
        AlValueCapture.DiffAndUpdate("OnRun", 1, new[] { Field("x", 5) }, lastKnown,
            isBaseline: false, assigned: new HashSet<string> { "x" });
        var next = AlValueCapture.DiffAndUpdate("OnRun", 2, new[] { Field("x", 5) }, lastKnown, isBaseline: false);
        Assert.Empty(next);
    }

    // --- per-scope frames -------------------------------------------------------------------

    [Fact]
    public void ScopeFrames_CalleeGetsItsOwnFrame_AndTheCallerFrameSurvivesTheCall()
    {
        var frames = new AlScopeFrames<string>(() => "fresh");
        var caller = new object();
        var callee = new object();

        var f1 = frames.GetOrPush(caller);
        f1.State = "caller-state";
        var f2 = frames.GetOrPush(callee);
        Assert.Equal("fresh", f2.State);
        Assert.NotSame(f1, f2);

        var popped = frames.Pop(callee);
        Assert.Same(f2, popped);
        Assert.Same(f1, frames.GetOrPush(caller));
        Assert.Equal("caller-state", frames.GetOrPush(caller).State);
    }

    [Fact]
    public void ScopeFrames_AFrameThatNeverExited_IsDiscardedWhenItsCallerResumes()
    {
        // An AL Error inside the callee can skip its Exit(); the next hit is the caller's.
        var frames = new AlScopeFrames<string>(() => "fresh");
        var caller = new object();
        var callee = new object();
        var f1 = frames.GetOrPush(caller);
        f1.State = "caller-state";
        frames.GetOrPush(callee);

        var resumed = frames.GetOrPush(caller);
        Assert.Same(f1, resumed);
        Assert.Equal(1, frames.Depth);
    }

    [Fact]
    public void ScopeFrames_PopOfAnUnknownScope_ReturnsNull_AndLeavesTheStackAlone()
    {
        var frames = new AlScopeFrames<string>(() => "fresh");
        frames.GetOrPush(new object());
        Assert.Null(frames.Pop(new object()));
        Assert.Equal(1, frames.Depth);
    }

    [Fact]
    public void ScopeFrames_Recursion_TwoInstancesOfTheSameScopeTypeAreTwoFrames()
    {
        var frames = new AlScopeFrames<string>(() => "fresh");
        var outer = new object();
        var inner = new object(); // a second instance of the same generated scope class
        var f1 = frames.GetOrPush(outer);
        f1.State = "outer";
        var f2 = frames.GetOrPush(inner);
        Assert.Equal("fresh", f2.State);
        frames.Pop(inner);
        Assert.Equal("outer", frames.GetOrPush(outer).State);
    }
}
