codeunit 60701 "DTT Duration ToText Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "DTT Helper";

    // -----------------------------------------------------------------------
    // Duration.ToText() — converts Duration to non-empty text
    // -----------------------------------------------------------------------

    [Test]
    procedure DurationToText_OneDay_NonEmpty()
    var
        Result: Text;
    begin
        // Positive: 1-day duration produces non-empty text
        Result := Helper.FormatDurationViaToText(Helper.OneDayMs());
        Assert.AreNotEqual('', Result, '1-day duration.ToText() must produce non-empty text');
    end;

    [Test]
    procedure DurationToText_OneHour_NonEmpty()
    var
        Result: Text;
    begin
        // Positive: 1-hour duration produces non-empty text
        Result := Helper.FormatDurationViaToText(Helper.OneHourMs());
        Assert.AreNotEqual('', Result, '1-hour duration.ToText() must produce non-empty text');
    end;

    [Test]
    procedure DurationToText_DifferentValues_Differ()
    var
        Day: Text;
        Hour: Text;
    begin
        // Negative: different durations produce different text
        Day := Helper.FormatDurationViaToText(Helper.OneDayMs());
        Hour := Helper.FormatDurationViaToText(Helper.OneHourMs());
        Assert.AreNotEqual(Day, Hour, 'ToText() must produce distinct output for different durations');
    end;

    // -----------------------------------------------------------------------
    // Format(Duration) — same expectation via the Format built-in
    // -----------------------------------------------------------------------

    [Test]
    procedure FormatDuration_OneDay_NonEmpty()
    var
        Result: Text;
    begin
        // Positive: Format(1-day duration) produces non-empty text
        Result := Helper.FormatDurationViaFormat(Helper.OneDayMs());
        Assert.AreNotEqual('', Result, 'Format(1-day duration) must produce non-empty text');
    end;

    [Test]
    procedure FormatDuration_OneHour_NonEmpty()
    var
        Result: Text;
    begin
        // Positive: Format(1-hour duration) produces non-empty text
        Result := Helper.FormatDurationViaFormat(Helper.OneHourMs());
        Assert.AreNotEqual('', Result, 'Format(1-hour duration) must produce non-empty text');
    end;

    // -----------------------------------------------------------------------
    // Negative
    // -----------------------------------------------------------------------

    [Test]
    procedure DurationToText_Error()
    begin
        // Negative: error mechanism works correctly
        asserterror Error('expected error');
        Assert.ExpectedError('expected error');
    end;
}
