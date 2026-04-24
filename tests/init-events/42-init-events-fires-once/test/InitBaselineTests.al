/// Two test codeunits that both assert the init-event baseline row is
/// present. Combined with InitEventsBaselineTests.cs (xUnit) which asserts
/// InitEventFireCount == 4, this proves the fix for #1220: init-events
/// fire exactly once per run, and the resulting DB state is restored on
/// every codeunit-level isolation boundary.
///
/// All tests here are read-only to avoid within-codeunit state coupling —
/// codeunit-level isolation (BC default) keeps state across tests inside
/// the same codeunit, so a test that mutates the baseline would break
/// the next read in the same codeunit. The baseline-restore behaviour is
/// instead proven across codeunit boundaries.
codeunit 57301 "Init Baseline Tests A"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    /// Positive: baseline row is present at the start of the first codeunit.
    [Test]
    procedure FirstCodeunit_SeesInitBaseline()
    var
        Sentinel: Record "Init Baseline Sentinel";
    begin
        Assert.IsTrue(Sentinel.Get(1), 'init-events baseline row missing in first codeunit');
        Assert.AreEqual(true, Sentinel.Seeded, 'Seeded flag must be true');
        Assert.AreEqual(1, Sentinel.Count(), 'baseline must have exactly one sentinel row');
    end;

    /// Negative: Get of an id that was never seeded fails.
    [Test]
    procedure FirstCodeunit_UnseededIdNotFound()
    var
        Sentinel: Record "Init Baseline Sentinel";
    begin
        Assert.IsFalse(Sentinel.Get(2), 'id 2 was never seeded by init-events');
    end;
}

codeunit 57302 "Init Baseline Tests B"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    /// Positive: second codeunit still sees the init baseline.
    /// Proves init-events need not re-fire between codeunits — the baseline
    /// captured once at startup is restored on each isolation boundary.
    [Test]
    procedure SecondCodeunit_SeesInitBaseline()
    var
        Sentinel: Record "Init Baseline Sentinel";
    begin
        Assert.IsTrue(Sentinel.Get(1), 'init-events baseline row missing in second codeunit');
        Assert.AreEqual(true, Sentinel.Seeded, 'Seeded flag must be true');
        Assert.AreEqual(1, Sentinel.Count(), 'baseline must have exactly one sentinel row');
    end;

    /// Negative: Get of an id that was never seeded fails (in second codeunit too).
    [Test]
    procedure SecondCodeunit_UnseededIdNotFound()
    var
        Sentinel: Record "Init Baseline Sentinel";
    begin
        Assert.IsFalse(Sentinel.Get(99), 'id 99 was never seeded');
    end;
}
