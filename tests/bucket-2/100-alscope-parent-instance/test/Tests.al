// Test suite for issue #1105 — AlScope.Parent instance access (CS0176).
//
// When AL procedures use `this.SomeVar := value;`, the BC compiler generates
// scope classes that access the parent codeunit via `this.Parent` (an instance
// reference on the AlScope base class). With AlScope.Parent as static, the
// generated C# fails with CS0176. The fix changes Parent to an instance property.
codeunit 98302 "Scope Parent Instance Tests"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    // Positive: Activate() uses `this.IsActivated := true` which generates a scope
    // class accessing Parent via instance. After the fix this should compile and run.
    [Test]
    procedure ScopeParentInstance_Activate_SetsTrue()
    var
        Src: Codeunit "Scope Parent Instance Src";
    begin
        Src.Reset();
        Src.Activate();
        Assert.IsTrue(Src.GetActivated(), 'IsActivated should be true after Activate()');
    end;

    // Positive: Deactivate() uses `this.IsActivated := false` — the exact pattern from
    // the telemetry that triggered CS0176 on AlScope.Parent.
    [Test]
    procedure ScopeParentInstance_Deactivate_SetsFalse()
    var
        Src: Codeunit "Scope Parent Instance Src";
    begin
        Src.Reset();
        Src.Activate();
        Src.Deactivate();
        Assert.IsFalse(Src.GetActivated(), 'IsActivated should be false after Deactivate()');
    end;

    // Positive: Increment() uses `this.Counter += Amount` — proves non-default value
    // so a no-op stub would not pass.
    [Test]
    procedure ScopeParentInstance_Increment_AccumulatesValue()
    var
        Src: Codeunit "Scope Parent Instance Src";
    begin
        Src.Reset();
        Src.Increment(5);
        Src.Increment(3);
        Assert.AreEqual(8, Src.GetCounter(), 'Counter should be 8 after two increments');
    end;

    // Negative: after Reset(), both IsActivated and Counter are cleared via this.X access.
    // Proves that the instance assignment through scope parent works correctly.
    [Test]
    procedure ScopeParentInstance_Reset_ClearsAll()
    var
        Src: Codeunit "Scope Parent Instance Src";
    begin
        Src.Activate();
        Src.Increment(42);
        Src.Reset();
        Assert.IsFalse(Src.GetActivated(), 'IsActivated should be false after Reset()');
        Assert.AreEqual(0, Src.GetCounter(), 'Counter should be 0 after Reset()');
    end;
}
