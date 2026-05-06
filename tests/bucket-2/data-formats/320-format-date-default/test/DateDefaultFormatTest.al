// Tests for issue #1603 — Format(Date) should produce BC locale format (MM/dd/yyyy),
// not XML/ISO-8601 (yyyy-MM-dd).  Format(Date, 0, 9) must produce ISO-8601.
//
// In BC, the report-dataset library stores date column values using the session locale
// format. A test that asserts Format(d, 0, 9) passes under al-runner but fails against
// a real BC container because the container stores '12/31/2026', not '2026-12-31'.
codeunit 1320211 "Date Default Format Test"
{
    Subtype = Test;

    var
        Helper: Codeunit "Date Default Format Helper";
        Assert: Codeunit Assert;

    // ─── Positive: default Format(Date) must use BC locale format ────────────

    [Test]
    procedure FormatDate_Default_ProducesLocaleFormat_Dec31()
    var
        Result: Text;
    begin
        // [GIVEN] Date 31 December 2026
        // [WHEN]  Format(d) — no format number
        Result := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        // [THEN]  BC default locale format for en-US is 'MM/dd/yyyy' → '12/31/2026'
        Assert.AreEqual('12/31/2026', Result, 'Format(Date) must produce BC locale format MM/dd/yyyy');
    end;

    [Test]
    procedure FormatDate_Default_ProducesLocaleFormat_Jan15()
    var
        Result: Text;
    begin
        // [GIVEN] Date 15 January 2025
        // [WHEN]  Format(d) — no format number
        Result := Helper.DefaultFormat(DMY2Date(15, 1, 2025));
        // [THEN]  BC locale format '01/15/2025'
        Assert.AreEqual('01/15/2025', Result, 'Format(Date) with day < 10 must zero-pad in MM/dd/yyyy');
    end;

    // ─── Positive: Format(Date, 0, 9) must produce ISO 8601 ─────────────────

    [Test]
    procedure FormatDate_Format9_ProducesIso8601_Dec31()
    var
        Result: Text;
    begin
        // [GIVEN] Date 31 December 2026
        // [WHEN]  Format(d, 0, 9) — format number 9 = XML Standard
        Result := Helper.XmlFormat(DMY2Date(31, 12, 2026));
        // [THEN]  ISO 8601 date '2026-12-31'
        Assert.AreEqual('2026-12-31', Result, 'Format(Date, 0, 9) must produce ISO 8601 yyyy-MM-dd');
    end;

    [Test]
    procedure FormatDate_Format9_ProducesIso8601_Jan15()
    var
        Result: Text;
    begin
        // [GIVEN] Date 15 January 2025
        // [WHEN]  Format(d, 0, 9)
        Result := Helper.XmlFormat(DMY2Date(15, 1, 2025));
        // [THEN]  ISO 8601 '2025-01-15'
        Assert.AreEqual('2025-01-15', Result, 'Format(Date, 0, 9) must produce ISO 8601 yyyy-MM-dd for January');
    end;

    // ─── Positive: default vs. format-9 must differ for the same date ────────

    [Test]
    procedure FormatDate_DefaultAndFormat9_ProduceDifferentStrings()
    var
        d: Date;
        Locale: Text;
        Xml: Text;
    begin
        // [GIVEN] Any date where day ≠ month (i.e. where ordering differs)
        d := DMY2Date(31, 12, 2026);
        Locale := Helper.DefaultFormat(d);
        Xml := Helper.XmlFormat(d);
        // [THEN]  The two strings are NOT equal — locale 12/31/2026 vs ISO 2026-12-31
        Assert.AreNotEqual(Locale, Xml, 'Format(d) and Format(d,0,9) must produce different strings');
    end;

    // ─── Positive: explicit format string path unaffected ────────────────────

    [Test]
    procedure FormatDate_ExplicitIsoString_StillProducesIso()
    var
        Result: Text;
    begin
        // [GIVEN] Date 1 January 2026
        // [WHEN]  Format(d, 0, '<Year4>-<Month,2>-<Day,2>') — explicit format string
        Result := Helper.ExplicitIsoFormat(DMY2Date(1, 1, 2026));
        // [THEN]  ISO 8601 '2026-01-01' — the explicit format string path must be unaffected
        Assert.AreEqual('2026-01-01', Result, 'Format with explicit <Year4>-<Month,2>-<Day,2> must still produce ISO 8601');
    end;

    // ─── Negative: Format(Date) must NOT produce ISO 8601 format ─────────────

    [Test]
    procedure FormatDate_Default_MustNotProduceIsoFormat()
    var
        Result: Text;
    begin
        // [GIVEN] Date where locale and ISO differ (day ≠ month)
        // [WHEN]  Format(d) — no format number
        Result := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        // [THEN]  Must NOT be ISO format '2026-12-31'
        Assert.AreNotEqual('2026-12-31', Result, 'Format(Date) must NOT produce ISO 8601; it must use locale format');
    end;
}
