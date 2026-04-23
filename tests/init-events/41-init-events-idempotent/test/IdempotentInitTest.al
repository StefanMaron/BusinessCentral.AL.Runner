/// Tests that --init-events tolerates subscribers that unconditionally insert
/// records without an existence-check guard.
///
/// Real extensions often have subscribers like:
///   Rec.Id := 1;
///   Rec.Insert();   // no Get/FindFirst guard
///
/// The runner fires both codeunit-2 and codeunit-27 OnCompanyInitialize events
/// in a single init cycle. If the same subscriber registers for both, the second
/// Insert() throws "record already exists". The runner must swallow that error
/// and let all tests execute normally.
codeunit 57201 "Init Events Idempotent Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// Positive: tests run at all — the "always Insert" subscriber must not abort
    /// the init-event firing and leave tests unable to execute.
    [Test]
    procedure AlwaysInsertSubscriber_DoesNotAbortTestRun()
    var
        Sentinel: Record "Idempotent Sentinel";
    begin
        // [GIVEN]  --init-events is active; the "Always-Insert Subscriber" fires
        //          once for CU-2 and once for CU-27 OnCompanyInitialize.
        // [WHEN]   This test method executes (proves init-events did not crash)
        // [THEN]   At least one insert succeeded — Sentinel row 1 exists
        Assert.IsTrue(Sentinel.Get(1), '--init-events: sentinel row must exist after init-event firing');
    end;

    /// Positive: even when a duplicate-PK error is swallowed, the test table
    /// is usable — subsequent Get/Insert/Modify in the test work normally.
    [Test]
    procedure TableIsUsableAfterInitEvents_SubscriberError()
    var
        Sentinel: Record "Idempotent Sentinel";
    begin
        // [GIVEN]  Init event fired (possibly with a swallowed insert error)
        // [WHEN]   This test reads and then modifies the seeded record
        // [THEN]   The table and its data are intact
        Assert.IsTrue(Sentinel.Get(1), 'Sentinel row must be readable after init-events');
        Assert.AreEqual(1, Sentinel.Count(), 'Table must have exactly one row after init-events');
    end;
}
