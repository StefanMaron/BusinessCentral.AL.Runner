// A test codeunit that never reads Event Subscription (2000000140) — #5099.
// EventSubscriptionInventoryLazinessTests runs ONLY this codeunit and asserts the runner did not
// build the inventory; the insert fires the table subscriber in "ESV Subscriber", so the bundle
// still has live subscribers wired for dispatch.
codeunit 70766 "ESV No Read Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "ESV Assert";

    [Test]
    procedure WatchedInsert_WithoutReadingEventSubscription_Succeeds()
    var
        Watched: Record "ESV Watched";
    begin
        Watched.Init();
        Watched."Entry No." := 1;
        Watched.Insert(true);
        Assert.AreEqual(1, Watched.Count(), 'the insert must land exactly one row');
    end;
}
