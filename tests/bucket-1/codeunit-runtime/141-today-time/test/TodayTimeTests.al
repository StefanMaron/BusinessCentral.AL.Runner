codeunit 60601 "TT Today Time Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "TT Helper";

    // -----------------------------------------------------------------------
    // Today — returns current date (non-zero, plausible range)
    // -----------------------------------------------------------------------

    [Test]
    procedure Today_IsNotZeroDate()
    begin
        // Positive: Today returns a non-zero date
        Assert.AreNotEqual(0D, Helper.GetToday(), 'Today must not return the zero date');
    end;

    [Test]
    procedure Today_IsAfter2000()
    begin
        // Positive: Today is after Jan 1 2000 (proves it returns a real current date)
        Assert.IsTrue(Helper.IsAfter2000(), 'Today must be after year 2000');
    end;

    [Test]
    procedure Today_IsBefore2100()
    begin
        // Positive: Today is before Jan 1 2100 (upper-bound sanity check)
        Assert.IsTrue(Helper.IsBefore2100(), 'Today must be before year 2100');
    end;

    [Test]
    procedure Today_Consistent()
    var
        d1: Date;
        d2: Date;
    begin
        // Positive: two consecutive calls to Today return the same date
        d1 := Helper.GetToday();
        d2 := Helper.GetToday();
        Assert.AreEqual(d1, d2, 'Two consecutive Today calls must return the same date');
    end;

    // -----------------------------------------------------------------------
    // Time — returns current time (non-zero)
    // -----------------------------------------------------------------------

    [Test]
    procedure Time_IsNotZero()
    begin
        // Positive: Time returns a non-zero time (runner has been running for at least a moment)
        Assert.AreNotEqual(0T, Helper.GetCurrentTime(), 'Time must not return the zero time');
    end;

    // -----------------------------------------------------------------------
    // Negative
    // -----------------------------------------------------------------------

    [Test]
    procedure TodayTime_Negative_Error()
    begin
        // Negative: error mechanism works correctly
        asserterror Error('expected error');
        Assert.ExpectedError('expected error');
    end;
}
