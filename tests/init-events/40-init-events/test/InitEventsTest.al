/// Tests that --init-events fires OnCompanyInitialize before test execution,
/// allowing subscribers on system codeunits to perform setup work.
codeunit 57101 "Init Events Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnCompanyInitialize_FiredBeforeTests_SentinelPresent()
    var
        Sentinel: Record "Init Events Sentinel";
    begin
        // [GIVEN]  The runner is started with --init-events
        // [WHEN]   Test execution begins (OnCompanyInitialize was fired before this test)
        // [THEN]   The sentinel record written by the subscriber exists
        Assert.IsTrue(Sentinel.Get(1), '--init-events must fire OnCompanyInitialize before test execution');
        Assert.IsTrue(Sentinel.Fired, 'Subscriber must have set Fired = true on the sentinel record');
    end;
}
