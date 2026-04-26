// Test suite for base.Parent.Bind() null-dereference fix.
// When an EventSubscriberInstance=Manual codeunit's procedure uses `this.Var` (scope class)
// AND calls BindSubscription(this), BC emits base.Parent.Bind() in the scope class.
// Before the fix: AlScope.Parent returns null → runtime binding on null → exception.
// After the fix: AlScope.Parent returns `this` → base.Parent.Bind() calls AlScope.Bind() (no-op).
codeunit 98502 "BPB Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // Positive: RegisterAndFire must not throw even though base.Parent.Bind() is in the scope.
    // BindCount starts at 0, RegisterAndFire increments it to 1 (this.BindCount += 1),
    // then BindSubscription fires the event which increments it to 2.
    [Test]
    procedure RegisterAndFire_ScopeBindUnbind_NoThrow()
    var
        Sub: Codeunit "BPB Subscriber";
        Pub: Codeunit "BPB Publisher";
    begin
        Sub.Reset();
        Sub.RegisterAndFire(Pub);
        // After RegisterAndFire:
        //   this.BindCount += 1 → 1
        //   BindSubscription(this) → base.Parent.Bind() must NOT throw
        //   Fire() → subscriber fires → BindCount += 1 → 2
        Assert.AreEqual(2, Sub.GetBindCount(), 'BindCount must be 2: 1 from this.BindCount += 1, 1 from event handler');
    end;

    // Positive: calling RegisterAndFire twice accumulates correctly,
    // proving the scope survives multiple invocations.
    [Test]
    procedure RegisterAndFire_TwoCalls_AccumulatesCount()
    var
        Sub: Codeunit "BPB Subscriber";
        Pub: Codeunit "BPB Publisher";
    begin
        Sub.Reset();
        Sub.RegisterAndFire(Pub);
        Sub.RegisterAndFire(Pub);
        // Each call: +1 from this.BindCount, +1 from event → +2 per call
        Assert.AreEqual(4, Sub.GetBindCount(), 'BindCount must be 4 after two RegisterAndFire calls');
    end;

    // Positive: after Reset(), counter is zero even after prior calls.
    [Test]
    procedure Reset_ClearsBindCount()
    var
        Sub: Codeunit "BPB Subscriber";
        Pub: Codeunit "BPB Publisher";
    begin
        Sub.RegisterAndFire(Pub);
        Sub.Reset();
        Assert.AreEqual(0, Sub.GetBindCount(), 'BindCount must be 0 after Reset()');
    end;
}
