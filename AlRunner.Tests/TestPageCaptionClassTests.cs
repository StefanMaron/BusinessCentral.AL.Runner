// Runner-mechanism tests for #4638: TestPage <field>.Caption() reads the control's CaptionClass
// through the page's "Control{id}_DynamicCaption" source expression. The BC-behaviour claim (what
// the caption IS) is pinned upstream, corpus codeunit 60930; these pin the runner's own lookup
// without loading the BC engine.
using System;
using System.Collections.Generic;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageCaptionClassTests
{
    private sealed class FakeExpression(string name, NavValue value)
    {
        public string Name { get; } = name;
        public NavValue Get() => value;
        public void Set(NavValue _) { }
    }

    private const int ControlId = 4638;

    private static RunnerPageInstance BuildPage(Dictionary<string, object?> expressions)
    {
        var ctor = typeof(RunnerPageInstance).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(object), typeof(object), typeof(NavRecord), typeof(int), typeof(System.Collections.IDictionary) },
            modifiers: null)
            ?? throw new InvalidOperationException("RunnerPageInstance private ctor not found.");
        return (RunnerPageInstance)ctor.Invoke(new object?[] { new object(), new object(), null, 4638001, expressions });
    }

    private static PageVariableTestField Field(Dictionary<string, object?> expressions)
        => new(BuildPage(expressions), new FakeExpression("CellData", new NavText("0")), ControlId,
               pageValidationErrors: null);

    [Fact]
    public void DynamicCaptionKey_IsBcsOwnKey()
    {
        // The compiled page registers the expression under NavForm.GetDynamicCaptionExpression's
        // key; if BC changes the spelling, every CaptionClass read silently falls back.
        var bcKey = typeof(NavForm).GetMethod("GetDynamicCaptionExpression",
                BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(int) })
            ?? throw new InvalidOperationException("NavForm.GetDynamicCaptionExpression(int) not found");
        Assert.Equal((string)bcKey.Invoke(null, new object[] { ControlId })!,
            RunnerPageInstance.DynamicCaptionExpressionKey(ControlId));
    }

    [Fact]
    public void Caption_ControlWithDynamicCaption_ReturnsItsValue()
    {
        var field = Field(new Dictionary<string, object?>
        {
            [$"Control{ControlId}_DynamicCaption"] = new FakeExpression("dyn", new NavText("Sep 2026")),
        });

        Assert.Equal("Sep 2026", field.Caption);
        // Name is not the caption: it stays the bound variable's name.
        Assert.Equal("CellData", field.Name);
    }

    [Fact]
    public void Caption_OtherControlsDynamicCaption_IsNotBorrowed()
    {
        var field = Field(new Dictionary<string, object?>
        {
            [$"Control{ControlId + 1}_DynamicCaption"] = new FakeExpression("dyn", new NavText("Oct 2026")),
        });

        Assert.Equal("CellData", field.Caption);
    }

    [Fact]
    public void Caption_EmptyDynamicCaption_FallsBackToStaticCaption()
    {
        var field = Field(new Dictionary<string, object?>
        {
            [$"Control{ControlId}_DynamicCaption"] = new FakeExpression("dyn", new NavText(string.Empty)),
        });

        Assert.Equal("CellData", field.Caption);
    }
}
