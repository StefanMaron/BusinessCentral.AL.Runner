// Renumbered from 60501 to avoid collision in new bucket layout (#1385).
codeunit 1060501 "CDT Create DateTime Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "CDT Helper";

    // -----------------------------------------------------------------------
    // CreateDateTime — construct a DateTime from Date + Time
    // -----------------------------------------------------------------------

    [Test]
    procedure CreateDateTime_RoundTrip_Positive()
    var
        d: Date;
        t: Time;
    begin
        // [GIVEN] A specific date and time
        // [WHEN]  CreateDateTime combines them, DT2Date/DT2Time decompose
        // [THEN]  The round-trip preserves both components
        d := DMY2Date(15, 6, 2024);
        t := 120000T;
        Assert.IsTrue(Helper.RoundTrip(d, t), 'CreateDateTime round-trip must preserve date and time');
    end;

    [Test]
    procedure CreateDateTime_RoundTrip_MidnightTime()
    var
        d: Date;
        t: Time;
    begin
        // Edge case: midnight (0T) round-trips correctly
        d := DMY2Date(1, 1, 2023);
        t := 000000T;
        Assert.IsTrue(Helper.RoundTrip(d, t), 'CreateDateTime round-trip must work at midnight');
    end;

    // -----------------------------------------------------------------------
    // DT2Date — extract the date component
    // -----------------------------------------------------------------------

    [Test]
    procedure DT2Date_ExtractsCorrectDate()
    var
        d: Date;
    begin
        // Positive: DT2Date returns exactly the date passed to CreateDateTime
        d := DMY2Date(1, 1, 2023);
        Assert.AreEqual(d, Helper.ExtractDate(Helper.MakeDateTime(d, 080000T)), 'DT2Date must return the date component');
    end;

    [Test]
    procedure DT2Date_DifferentDates_Distinct()
    var
        d1: Date;
        d2: Date;
    begin
        // Negative: two different dates produce different DT2Date results
        d1 := DMY2Date(1, 1, 2023);
        d2 := DMY2Date(2, 1, 2023);
        Assert.AreNotEqual(
            Helper.ExtractDate(Helper.MakeDateTime(d1, 120000T)),
            Helper.ExtractDate(Helper.MakeDateTime(d2, 120000T)),
            'DT2Date must distinguish different dates');
    end;

    [Test]
    procedure DT2Date_ZeroDateTime_IsZeroDate()
    begin
        // Edge case: zero DateTime (0DT) decomposes to zero date (0D)
        Assert.IsTrue(Helper.ZeroDateTimeIsZeroDate(), 'DT2Date of zero DateTime must be 0D');
    end;

    // -----------------------------------------------------------------------
    // DT2Time — extract the time component
    // -----------------------------------------------------------------------

    [Test]
    procedure DT2Time_ExtractsCorrectTime()
    var
        d: Date;
        t: Time;
    begin
        // Positive: DT2Time returns exactly the time passed to CreateDateTime
        d := DMY2Date(15, 6, 2024);
        t := 153000T;
        Assert.AreEqual(t, Helper.ExtractTime(Helper.MakeDateTime(d, t)), 'DT2Time must return the time component');
    end;

    [Test]
    procedure DT2Time_DifferentTimes_Distinct()
    var
        d: Date;
    begin
        // Negative: two different times produce different DT2Time results
        d := DMY2Date(1, 1, 2023);
        Assert.AreNotEqual(
            Helper.ExtractTime(Helper.MakeDateTime(d, 080000T)),
            Helper.ExtractTime(Helper.MakeDateTime(d, 090000T)),
            'DT2Time must distinguish different times');
    end;

    // -----------------------------------------------------------------------
    // Variant round-trip — exercises the ALDaTi2Variant lowering path
    // -----------------------------------------------------------------------

    [Test]
    procedure CreateDateTime_Variant_RoundTrip_Positive()
    var
        d: Date;
        t: Time;
    begin
        // Positive: CreateDateTime result boxed through a Variant preserves both
        // components when unboxed and decomposed. Exercises ALDaTi2Variant.
        d := DMY2Date(15, 6, 2024);
        t := 153045T;
        Assert.IsTrue(
            Helper.VariantRoundTrip(d, t),
            'CreateDateTime via Variant must round-trip');
    end;

    [Test]
    procedure CreateDateTime_Variant_DifferentTimes_Distinct()
    var
        d: Date;
        dt1: DateTime;
        dt2: DateTime;
    begin
        // Negative: different times produce distinguishable round-tripped times
        // even when boxed through a Variant.
        d := DMY2Date(1, 1, 2023);
        dt1 := Helper.VariantMake(d, 080000T);
        dt2 := Helper.VariantMake(d, 090000T);
        Assert.AreNotEqual(
            Helper.ExtractTime(dt1),
            Helper.ExtractTime(dt2),
            'Variant round-trip must distinguish different times');
    end;
}
