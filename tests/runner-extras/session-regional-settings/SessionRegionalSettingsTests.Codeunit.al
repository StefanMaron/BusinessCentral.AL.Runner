// The skeleton NavSession is created with RuntimeHelpers.GetUninitializedObject, so its
// `cultureSettings` field stayed default(ClientSettings) — every string pattern null.
//
// ClientSettings is a STRUCT, so BC's own fallback in NavSessionOrDefaultProvider
// (`session?.RegionalSettings ?? DefaultRegionalSettings`) can never fire: the property
// is never null, it is just empty. NavDateTimeEvaluator.NonXMLFormatEvaluate hands those
// null patterns to DateTimeParsingHelper.CreateExactDateTimePatterns, which does
// `longTimePattern.Replace(...)` → bare NullReferenceException.
//
// RED (before the fix): the first Evaluate below dies with
//   "NullReferenceException: Object reference not set to an instance of an object."
// GREEN (after the fix): the skeleton session carries a fully-populated ClientSettings,
// built exactly the way BC's own AppInitFallbackValues builds its default one.
//
// Found via Pageworks: Spare Brained Licensing's CheckIfActive does an Evaluate into a
// DateTime on every licence check, so the NRE took out 85 otherwise-unrelated tests.
codeunit 61942 "SRS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SRS Assert";

    [Test]
    procedure EvaluateIntoDateTimeSucceedsOnTheSkeletonSession()
    var
        Dt: DateTime;
        Ok: Boolean;
    begin
        // ISO-ish round-trip literal — Evaluate accepts the invariant "O" form regardless
        // of the session's regional patterns, but reaching that branch still walks the
        // pattern-based branch first, which is where the NRE was.
        Ok := Evaluate(Dt, '2026-01-02T10:11:12.0000000Z', 9);
        Assert.IsTrue(Ok, 'Evaluate of an XML-format DateTime literal must succeed');
        Assert.IsFalse(Dt = 0DT, 'the evaluated DateTime must not be the null DateTime');
    end;

    [Test]
    procedure EvaluateIntoDateTimeUsesTheSessionRegionalPatterns()
    var
        Dt: DateTime;
        Ok: Boolean;
    begin
        // No format number → the session's ShortDatePattern / LongTimePattern drive parsing.
        // This is the exact call shape that NRE'd (CreateExactDateTimePatterns).
        Ok := Evaluate(Dt, Format(CurrentDateTime));
        Assert.IsTrue(Ok, 'Evaluate of a session-formatted DateTime must round-trip');
        Assert.IsFalse(Dt = 0DT, 'the round-tripped DateTime must not be the null DateTime');
    end;

    [Test]
    procedure EvaluateIntoDateAndTimeSucceed()
    var
        D: Date;
        T: Time;
    begin
        Assert.IsTrue(Evaluate(D, Format(Today)), 'Evaluate into a Date must succeed');
        Assert.IsTrue(D <> 0D, 'the evaluated Date must not be the null Date');
        Assert.IsTrue(Evaluate(T, Format(Time)), 'Evaluate into a Time must succeed');
    end;

    // Negative direction: garbage must be REJECTED (return false), not crash and not
    // silently produce a value. Without this the tests above would still pass against an
    // implementation that made Evaluate always succeed.
    [Test]
    procedure EvaluateRejectsNonDateTimeText()
    var
        Dt: DateTime;
        Ok: Boolean;
    begin
        Ok := Evaluate(Dt, 'not a datetime at all');
        Assert.IsFalse(Ok, 'Evaluate of non-date text must return false');
        Assert.IsTrue(Dt = 0DT, 'a rejected Evaluate must leave the DateTime at its null value');
    end;

    [Test]
    procedure EvaluateWithoutTrapRaisesAnAlErrorForNonDateTimeText()
    var
        Dt: DateTime;
    begin
        // Ignoring Evaluate's return value makes it raise on failure. That error must be a
        // real, trappable AL error — proving the failure path is an AL error and not a
        // runner NullReferenceException (which `asserterror` cannot trap).
        asserterror Evaluate(Dt, 'not a datetime at all', 9);
    end;
}
