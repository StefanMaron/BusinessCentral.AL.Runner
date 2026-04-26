// Test suite for issue #1092 — AlScope.Parent static property.
//
// BC scope classes inherit Parent from NavMethodScope<T>. After the rewriter
// replaces NavMethodScope<T> with AlScope, some BC compiler versions emit
// AlScope.Parent as a static member access, causing CS0117. The fix adds
// a static null-returning stub on AlScope so the generated C# compiles.
//
// These tests exercise codeunit-level variable access through scope classes
// (the base.Parent.xxx rewrite) and verify correct runtime behaviour.
codeunit 60291 "Scope Parent Tests"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    // Positive: scope class can access parent codeunit variables via the
    // base.Parent.xxx → _parent.xxx rewrite. Verifies basic parent access works.
    [Test]
    procedure ScopeParent_SingleStep_CounterIsOne()
    var
        Src: Codeunit "Scope Parent Source";
    begin
        Src.Reset();
        Src.ExecuteStep('Init');
        Assert.AreEqual(1, Src.GetStepCounter(), 'Step counter should be 1 after one step');
    end;

    // Positive: multiple calls accumulate correctly through the parent reference.
    // Proves the _parent instance is shared across scope invocations.
    [Test]
    procedure ScopeParent_MultiStep_CounterAccumulates()
    var
        Src: Codeunit "Scope Parent Source";
    begin
        Src.Reset();
        Src.ExecuteStep('Alpha');
        Src.ExecuteStep('Beta');
        Src.ExecuteStep('Gamma');
        Assert.AreEqual(3, Src.GetStepCounter(), 'Step counter should be 3 after three steps');
    end;

    // Positive: last step name is tracked correctly via parent variable access.
    [Test]
    procedure ScopeParent_LastStep_CorrectValue()
    var
        Src: Codeunit "Scope Parent Source";
    begin
        Src.Reset();
        Src.ExecuteStep('First');
        Src.ExecuteStep('Second');
        Assert.AreEqual('Second', Src.GetLastStep(), 'LastStep should be the most recent step name');
    end;

    // Positive: reset clears the counter through parent access.
    [Test]
    procedure ScopeParent_Reset_ClearsCounter()
    var
        Src: Codeunit "Scope Parent Source";
    begin
        Src.ExecuteStep('BeforeReset');
        Src.Reset();
        Assert.AreEqual(0, Src.GetStepCounter(), 'Counter should be 0 after reset');
    end;

    // Negative: after reset, last step is empty.
    // Proves that reset actually clears state rather than leaving stale values.
    [Test]
    procedure ScopeParent_Reset_ClearsLastStep()
    var
        Src: Codeunit "Scope Parent Source";
    begin
        Src.ExecuteStep('BeforeReset');
        Src.Reset();
        Assert.AreEqual('', Src.GetLastStep(), 'LastStep should be empty after reset');
    end;
}
