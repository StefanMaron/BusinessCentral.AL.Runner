// Tests for issue #1603 — Format(Date) locale configuration.
//
// Default behaviour (no --date-locale / ALRUNNER_DATE_LOCALE):
//   Format(Date) → ISO-8601 yyyy-MM-dd  (historical runner default, unchanged)
//   Format(Date, 0, 9) → ISO-8601 yyyy-MM-dd  (explicit BC format-9 = XML Standard)
//
// Configured behaviour (SetDateLocale('en-US')):
//   Format(Date) → MM/dd/yyyy  (matches a US-localized BC container's session locale)
//
// The SetDateLocale / GetDateLocale / FormatDate methods on codeunit 131100 "AL Runner Config"
// allow individual tests to assert against the locale format without needing a CLI flag.
codeunit 1320211 "Date Default Format Test"
{
    Subtype = Test;

    var
        Helper: Codeunit "Date Default Format Helper";
        Config: Codeunit "AL Runner Config";
        Assert: Codeunit Assert;

    // ─── Positive: default Format(Date) must remain ISO-8601 ─────────────────
    //
    // The runner's historical default is yyyy-MM-dd (invariant / ISO-8601).
    // Do NOT change this — existing users on that default must see no diff.

    [Test]
    procedure FormatDate_DefaultLocale_ProducesIso8601_Dec31()
    var
        Result: Text;
    begin
        // [GIVEN] No date locale configured (default)
        Config.SetDateLocale('');
        // [WHEN]  Format(d) — no format number
        Result := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        // [THEN]  Historical default: ISO-8601 yyyy-MM-dd
        Assert.AreEqual('2026-12-31', Result, 'Format(Date) with no locale must produce ISO-8601 yyyy-MM-dd');
    end;

    [Test]
    procedure FormatDate_DefaultLocale_ProducesIso8601_Jan15()
    var
        Result: Text;
    begin
        // [GIVEN] No date locale configured (default)
        Config.SetDateLocale('');
        // [WHEN]  Format(d) — no format number
        Result := Helper.DefaultFormat(DMY2Date(15, 1, 2025));
        // [THEN]  Historical default: ISO-8601 yyyy-MM-dd
        Assert.AreEqual('2025-01-15', Result, 'Format(Date) with no locale must produce ISO-8601 zero-padded');
    end;

    // ─── Positive: Format(Date, 0, 9) always produces ISO-8601 ───────────────

    [Test]
    procedure FormatDate_Format9_ProducesIso8601_Regardless()
    var
        Result: Text;
    begin
        // [GIVEN] Locale is set to en-US
        Config.SetDateLocale('en-US');
        // [WHEN]  Format(d, 0, 9) — BC format number 9 = XML Standard
        Result := Helper.XmlFormat(DMY2Date(31, 12, 2026));
        // [THEN]  Must always return ISO-8601 regardless of the configured locale
        Assert.AreEqual('2026-12-31', Result, 'Format(Date, 0, 9) must always produce ISO-8601');
        Config.SetDateLocale('');
    end;

    // ─── Positive: configured locale changes default Format(Date) output ──────

    [Test]
    procedure FormatDate_LocaleEnUS_ProducesUSFormat_Dec31()
    var
        Result: Text;
    begin
        // [GIVEN] Date locale = en-US
        Config.SetDateLocale('en-US');
        // [WHEN]  Format(d) — no format number
        Result := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        // [THEN]  en-US short date = MM/dd/yyyy → 12/31/2026
        Assert.AreEqual('12/31/2026', Result, 'Format(Date) with en-US locale must produce MM/dd/yyyy');
        Config.SetDateLocale('');
    end;

    [Test]
    procedure FormatDate_LocaleEnUS_ProducesUSFormat_Jan15()
    var
        Result: Text;
    begin
        // [GIVEN] Date locale = en-US
        Config.SetDateLocale('en-US');
        // [WHEN]  Format(d)
        Result := Helper.DefaultFormat(DMY2Date(15, 1, 2025));
        // [THEN]  en-US short date pattern M/d/yyyy → 1/15/2025 (no leading zero for single-digit month)
        //         Note: en-US uses M/d/yyyy (no leading zeros), not MM/dd/yyyy.
        Assert.AreEqual('1/15/2025', Result, 'Format(Date) with en-US locale must produce M/d/yyyy short date');
        Config.SetDateLocale('');
    end;

    // ─── Positive: clearing locale restores ISO-8601 default ─────────────────

    [Test]
    procedure FormatDate_ClearLocale_RestoresIsoDefault()
    var
        Before: Text;
        After: Text;
    begin
        // [GIVEN] Locale set then cleared
        Config.SetDateLocale('en-US');
        Before := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        Config.SetDateLocale('');
        After := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        // [THEN]  Before differs from after, and after is ISO-8601
        Assert.AreEqual('2026-12-31', After, 'Clearing date locale must restore ISO-8601 default');
        Assert.AreNotEqual(Before, After, 'en-US and default locale must produce different strings');
    end;

    // ─── Positive: GetDateLocale round-trips SetDateLocale ───────────────────

    [Test]
    procedure GetDateLocale_ReturnsSetValue()
    var
        Result: Text;
    begin
        // [GIVEN] Locale set to en-US
        Config.SetDateLocale('en-US');
        // [WHEN]  GetDateLocale()
        Result := Config.GetDateLocale();
        // [THEN]  Returns 'en-US' (or equivalent BCP-47 form)
        Assert.IsTrue(StrPos(Result, 'en') > 0, 'GetDateLocale must return a value containing the configured culture');
        Config.SetDateLocale('');
    end;

    // ─── Positive: explicit format string path is unaffected ─────────────────

    [Test]
    procedure FormatDate_ExplicitIsoString_StillProducesIso()
    var
        Result: Text;
    begin
        // [GIVEN] Locale set to en-US
        Config.SetDateLocale('en-US');
        // [WHEN]  Format(d, 0, '<Year4>-<Month,2>-<Day,2>') — explicit format string
        Result := Helper.ExplicitIsoFormat(DMY2Date(1, 1, 2026));
        // [THEN]  Explicit format string wins over locale setting
        Assert.AreEqual('2026-01-01', Result, 'Format with explicit format string must ignore locale setting');
        Config.SetDateLocale('');
    end;

    // ─── Negative: default locale must NOT match US format ───────────────────

    [Test]
    procedure FormatDate_DefaultLocale_MustNotMatchUSFormat()
    var
        Result: Text;
    begin
        // [GIVEN] No locale configured
        Config.SetDateLocale('');
        // [WHEN]  Format(d) for a date where day ≠ month
        Result := Helper.DefaultFormat(DMY2Date(31, 12, 2026));
        // [THEN]  Must NOT be '12/31/2026' (that is the US locale format, not the default)
        Assert.AreNotEqual('12/31/2026', Result, 'Default locale must NOT produce US format; use ISO-8601');
    end;
}
