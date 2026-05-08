using System.Linq;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

public class IterationTrackerStepCapturesTests
{
    [Fact]
    public void FinalizeIteration_ReadsCapturesAndMessagesFromActiveTestExecutionScope()
    {
        // Plan E4 regression repro: the old FinalizeIteration sampled
        // ValueCapture.GetCaptures() / MessageCapture.GetMessages() — the
        // GLOBAL aggregates, only populated when Enable() was called. In
        // the v2 streaming Executor.RunTests path, captures and messages
        // go to TestExecutionScope.Current only — the global stays empty,
        // so step.CapturedValues and step.Messages were always [].
        IterationTracker.Reset();
        IterationTracker.Enable();

        // Open a per-test scope (the actual API is TestExecutionScope.Begin
        // returning IDisposable; see AlRunner/TestExecutionScope.cs:21).
        using var _ = AlRunner.TestExecutionScope.Begin("UnitTestProc");

        var loopId = IterationTracker.EnterLoop("ScopeA", sourceStartLine: 10, sourceEndLine: 12);

        // Iteration 1
        IterationTracker.EnterIteration(loopId);
        ValueCapture.Capture("ScopeA", "ObjA", "i", 1, statementId: 0);
        ValueCapture.Capture("ScopeA", "ObjA", "sum", 1, statementId: 1);

        // Iteration 2
        IterationTracker.EnterIteration(loopId);
        ValueCapture.Capture("ScopeA", "ObjA", "i", 2, statementId: 0);
        ValueCapture.Capture("ScopeA", "ObjA", "sum", 3, statementId: 1);

        IterationTracker.ExitLoop(loopId);

        var loops = IterationTracker.GetLoops();
        Assert.Single(loops);
        var loop = loops[0];
        Assert.Equal(2, loop.IterationCount);
        Assert.Equal(2, loop.Steps.Count);

        var step1 = loop.Steps[0];
        Assert.Equal(1, step1.Iteration);
        Assert.Equal(2, step1.CapturedValues.Count);
        Assert.Contains(step1.CapturedValues, cv => cv.VariableName == "i" && cv.Value == "1");
        Assert.Contains(step1.CapturedValues, cv => cv.VariableName == "sum" && cv.Value == "1");

        var step2 = loop.Steps[1];
        Assert.Equal(2, step2.Iteration);
        Assert.Equal(2, step2.CapturedValues.Count);
        Assert.Contains(step2.CapturedValues, cv => cv.VariableName == "i" && cv.Value == "2");
        Assert.Contains(step2.CapturedValues, cv => cv.VariableName == "sum" && cv.Value == "3");

        IterationTracker.Reset();
    }

    [Fact]
    public void FinalizeIteration_ReadsMessagesFromActiveTestExecutionScope()
    {
        // Parallel test for the messages branch of the same bug.
        IterationTracker.Reset();
        IterationTracker.Enable();

        using var _ = AlRunner.TestExecutionScope.Begin("UnitMessageProc");

        var loopId = IterationTracker.EnterLoop("ScopeM", sourceStartLine: 5, sourceEndLine: 8);

        IterationTracker.EnterIteration(loopId);
        AlRunner.Runtime.MessageCapture.Capture("hello-1");

        IterationTracker.EnterIteration(loopId);
        AlRunner.Runtime.MessageCapture.Capture("hello-2");

        IterationTracker.ExitLoop(loopId);

        var loop = IterationTracker.GetLoops().Single();
        Assert.Equal(2, loop.Steps.Count);
        Assert.Equal(new[] { "hello-1" }, loop.Steps[0].Messages);
        Assert.Equal(new[] { "hello-2" }, loop.Steps[1].Messages);

        IterationTracker.Reset();
    }
}
