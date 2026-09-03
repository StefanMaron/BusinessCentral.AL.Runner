// The open-time snapshot behind a control's own Visible.
//
// Measured on all 8 BC versions through corpus PR #125: after a TestPage changes a page global
// that a control's Visible is bound to, real BC keeps reporting the value from when the page was
// opened, while the SAME control's Editable and Enabled follow the change. A group's Visible
// follows it too — corpus TestPageFieldVisibleGroup_Tests flips a group's Visible expression
// after the page is open, reads a field inside it as newly visible, and is green on real BC.
//
// So exactly one property is frozen and everything around it is live. These tests pin the
// mechanism that makes that possible: RunnerPageInstance.SnapshotExpressionValues reads every
// registered source expression once, and a later change to the same expression must move a live
// read while leaving the snapshot alone. Each half is the other's control — a snapshot that
// tracked changes would fail the first test, and a "snapshot" that was really a constant would
// fail the second.
//
// This is a runner-internal claim. What BC ANSWERS is the corpus's business, not a C# test's.

using System.Collections;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerPageInstanceVisibleSnapshotTests
{
    /// <summary>
    /// Stands in for one entry of NavForm.SourceExpressions. RunnerPageInstance.GetValue finds
    /// the parameterless Get() by reflection, so a plain class with that shape is enough — and
    /// using the real GetValue is the point: a fake that bypassed it would prove nothing about
    /// how the snapshot actually reads an expression.
    /// </summary>
    private sealed class FakeExpression
    {
        internal bool Current;
        public NavValue Get() => NavBoolean.Create(Current);
    }

    private sealed class ThrowingExpression
    {
        public NavValue Get() => throw new InvalidOperationException("this expression cannot be read yet");
    }

    private static IDictionary Table(params (string Name, object Expression)[] entries)
    {
        var table = new Hashtable();
        foreach (var (name, expression) in entries) table[name] = expression;
        return table;
    }

    // The frozen half. The snapshot is taken while the expression reads false; flipping the
    // expression to true afterwards must not move it.
    [Fact]
    public void Snapshot_KeepsTheValueFromWhenItWasTaken()
    {
        var expression = new FakeExpression { Current = false };
        var snapshot = RunnerPageInstance.SnapshotExpressionValues(Table(("p1p1HideIt", expression)));

        expression.Current = true;

        Assert.True(snapshot.TryGetValue("p1p1HideIt", out var frozen));
        Assert.Equal(false, frozen);
    }

    // The live half, and the control for the test above: the SAME change the snapshot ignores
    // must be visible to a live read through the same accessor the runner uses for Editable and
    // Enabled. Without this, a snapshot that always answered false would look correct.
    [Fact]
    public void ALiveRead_SeesTheChangeTheSnapshotIgnores()
    {
        var expression = new FakeExpression { Current = false };
        var snapshot = RunnerPageInstance.SnapshotExpressionValues(Table(("p1p1HideIt", expression)));

        expression.Current = true;

        Assert.Equal(false, snapshot["p1p1HideIt"]);
        Assert.Equal(true, RunnerPageInstance.GetValue(expression)?.ClientObject);
    }

    // Every registered expression is captured, not just the first, and each keeps its own value.
    [Fact]
    public void Snapshot_CapturesEveryRegisteredExpressionSeparately()
    {
        var hide = new FakeExpression { Current = true };
        var lockIt = new FakeExpression { Current = false };

        var snapshot = RunnerPageInstance.SnapshotExpressionValues(
            Table(("p1p1HideIt", hide), ("p1p1LockIt", lockIt)));

        Assert.Equal(true, snapshot["p1p1HideIt"]);
        Assert.Equal(false, snapshot["p1p1LockIt"]);
    }

    // Negative: one expression that cannot be read at open time must not cost the page its whole
    // snapshot. It is omitted, so the control bound to it falls back to a live read — which is
    // what happened before the snapshot existed, and which keeps the loud failure at the read
    // that asks for it rather than turning it into a page-construction failure.
    [Fact]
    public void AnExpressionThatCannotBeRead_IsOmittedWithoutLosingTheOthers()
    {
        var readable = new FakeExpression { Current = true };

        var snapshot = RunnerPageInstance.SnapshotExpressionValues(
            Table(("p1p1Broken", new ThrowingExpression()), ("p1p1HideIt", readable)));

        Assert.False(snapshot.ContainsKey("p1p1Broken"));
        Assert.Equal(true, snapshot["p1p1HideIt"]);
    }

    // A null entry is not an expression and must not be snapshotted as a null VALUE — that would
    // make a control bound to it read "evaluated to null, which is not a Boolean" from the
    // snapshot instead of falling through to the page's own binding lookup.
    [Fact]
    public void ANullEntry_IsNotSnapshotted()
    {
        var table = new Hashtable { ["p1p1Missing"] = null };

        var snapshot = RunnerPageInstance.SnapshotExpressionValues(table);

        Assert.False(snapshot.ContainsKey("p1p1Missing"));
    }
}
