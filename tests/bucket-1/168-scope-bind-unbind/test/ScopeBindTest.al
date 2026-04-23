codeunit 168003 "SBU Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure BindSubscription_ScopeCompiles_NoThrow()
    var
        Pub: Codeunit "SBU Publisher";
        Sub: Codeunit "SBU Manual Sub";
        C: Record "SBU Counter";
    begin
        // Positive: BindSubscription on the codeunit compiles and runs.
        // This exercises the _parent.Bind() path (AlScope.Bind + codeunit.Bind injection).
        BindSubscription(Sub);
        Pub.Fire();

        Assert.IsTrue(C.Get(1), 'Counter should exist after subscriber fires');
        Assert.AreEqual(1, C.HitCount, 'Subscriber should have fired exactly once');

        UnbindSubscription(Sub);
    end;

    [Test]
    procedure UnbindSubscription_SubsequentFire_NoHit()
    var
        Pub: Codeunit "SBU Publisher";
        Sub: Codeunit "SBU Manual Sub";
        C: Record "SBU Counter";
    begin
        // Negative: after UnbindSubscription, the subscriber must not fire.
        BindSubscription(Sub);
        Pub.Fire();

        // Should have fired once
        Assert.IsTrue(C.Get(1), 'Counter should exist');
        Assert.AreEqual(1, C.HitCount, 'Should be 1 after first fire');

        UnbindSubscription(Sub);
        Pub.Fire();  // second fire — subscriber is unbound, no hit

        C.Get(1);
        Assert.AreEqual(1, C.HitCount, 'Should still be 1 after firing unbound subscriber');
    end;
}
