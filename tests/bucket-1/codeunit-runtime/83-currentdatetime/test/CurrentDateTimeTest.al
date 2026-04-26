codeunit 50831 "CDT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "CDT Src";

    [Test]
    procedure CurrentDateTime_IsNotZero()
    var
        now: DateTime;
    begin
        // Positive: CurrentDateTime must not return the default 0DT sentinel
        // (which would indicate the built-in is unwired and returning default(DateTime)).
        now := Src.GetNow();
        Assert.AreNotEqual(0DT, now, 'CurrentDateTime must not be the zero sentinel');
    end;

    [Test]
    procedure CurrentDateTime_IsAfter2000()
    begin
        // Positive: the returned value must be later than 2000-01-01, ruling out
        // stubs that might return a low-epoch default.
        Assert.IsTrue(Src.IsAfter2000(), 'CurrentDateTime must be after 2000-01-01');
    end;

    [Test]
    procedure CurrentDateTime_IsBefore2200()
    begin
        // Positive: the value must be before 2200-01-01, ruling out stubs that
        // return DateTime.MaxValue or similar far-future sentinels.
        Assert.IsTrue(Src.IsBefore2200(), 'CurrentDateTime must be before 2200-01-01');
    end;

    [Test]
    procedure CurrentDateTime_DT2Date_IsNotZero()
    var
        d: Date;
    begin
        // Positive: DT2Date(CurrentDateTime) yields a non-zero date —
        // proves the DateTime value carries a date component.
        d := Src.GetDT2Date();
        Assert.AreNotEqual(0D, d, 'DT2Date(CurrentDateTime) must not be 0D');
    end;

    [Test]
    procedure CurrentDateTime_ConsecutiveReads_AreMonotonic()
    begin
        // Negative guard for caching: a stub that cached CurrentDateTime in a
        // constant would always return equal values; a stub that re-evaluated
        // against a buggy clock might return b < a. The assertion `b >= a`
        // holds for any reasonable clock including ties on fast CPUs.
        Assert.IsTrue(Src.TwoReads_InOrder(),
            'Two consecutive CurrentDateTime reads must be monotonic non-decreasing');
    end;

    [Test]
    procedure CurrentDateTime_NotHardcodedPast_NegativeTrap()
    var
        unix1970: DateTime;
    begin
        // Negative: guard against a stub returning a hardcoded 1970 epoch.
        unix1970 := CreateDateTime(DMY2Date(1, 1, 1970), 000000T);
        Assert.IsTrue(Src.GetNow() > unix1970,
            'CurrentDateTime must not be a hardcoded 1970 epoch');
    end;
}
