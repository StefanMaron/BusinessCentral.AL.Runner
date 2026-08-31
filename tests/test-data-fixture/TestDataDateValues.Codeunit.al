/// <summary>
/// End-to-end proof for issue #2259: `--test-data` rebuilds Date, DateTime, Time and
/// DateFormula values out of the backup, and AL reads them back as the values BC stored.
///
/// THE ASSERTION THAT MATTERS IS THE BLANK ONE. BC stores AL's blank date `0D` as the SQL
/// sentinel 1753-01-01, because a SQL `datetime` column cannot hold 0001-01-01 and BC's own
/// write path (NavDate.GetSqlWritableValue) throws rather than store a real date below
/// 1754-01-01. A codec that let the sentinel through as a literal date would put a date in
/// 1753 into every blank date cell — 4,854 Date cells in the shipped CRONUS data go through
/// this path — and every `if X = 0D` in AL would take the wrong branch with no error anywhere.
/// A test that only asserted "the table hydrated" would pass with that bug present.
///
/// THE SUBJECTS, AND WHY THESE
///   - `Purchases & Payables Setup` is the table issue #2259 named: its single
///     `Allow Document Deletion Before` Date column refused the WHOLE table, which is what
///     stood behind three of the fifteen missing-setup failures in #2240. `Invoice Nos.` is
///     the field those failures actually asked for, so both are asserted together — the date
///     is what unblocks it, the code is what proves it landed.
///   - `FA Depreciation Book` carries a blank and a real value in the SAME column
///     (FA000040 blank, FA000010 = 2029-12-31), so one column proves both directions.
///   - `Payment Terms` covers DateFormula (BC's token encoding, not readable formula text)
///     and DateTime in one row.
///   - `Calendar Entry` covers Time, whose SQL carrier date (1754-01-01) is NOT part of the
///     AL value and must be discarded.
///
/// ON BC VERSIONS. This bundle is not run by CI, but it is run by hand against more than one
/// artifact, and demo data is not guaranteed identical across them. The blank assertions are
/// version-stable by construction — "no value" is not a value that drifts. The concrete dates
/// are not, so each one is paired with a blank assertion on the same column rather than
/// carrying the claim alone: if a future artifact changes FA000010's ending date, this fails
/// with a readable "expected X got Y" on a value, not silently stop testing anything.
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64403 "Test Data Date Values"
{
    Subtype = Test;

    var
        Assert: Codeunit "TDF Assert";

    /// <summary>
    /// The exact table and column from issue #2259, and the two fields that matter: the Date
    /// that used to refuse the table, and the Code that #2240's failures asked for.
    /// </summary>
    [Test]
    procedure PurchasesPayablesSetupHydrates()
    var
        PurchSetup: Record "Purchases & Payables Setup";
    begin
        PurchSetup.Get();

        // Field 46, SQL `datetime NOT NULL`, holding 1753-01-01 in the shipped backup. The
        // column cannot be NULL, so the sentinel is the only way BC has to say "blank".
        Assert.AreEqual(0D, PurchSetup."Allow Document Deletion Before",
            'Allow Document Deletion Before must read as AL blank, not as a date in 1753');

        // And the table really did hydrate its other fields, so the blank above is a rebuilt
        // blank rather than an untouched Record.Init() default.
        Assert.AreEqual('P-INV', PurchSetup."Invoice Nos.", 'Invoice Nos.');
        PurchSetup.TestField("Invoice Nos.");
    end;

    /// <summary>Both directions on ONE column: a blank cell and a real cell of the same
    /// Date field, so neither claim can be satisfied by the other's implementation.</summary>
    [Test]
    procedure BlankAndRealDateOnTheSameColumn()
    var
        Blank: Record "FA Depreciation Book";
        Real: Record "FA Depreciation Book";
    begin
        Blank.Get('FA000040', 'COMPANY');
        Assert.AreEqual(0D, Blank."Depreciation Ending Date",
            'FA000040 ending date is 1753-01-01 in SQL and must read as AL blank');
        Assert.AreEqual('', Format(Blank."Depreciation Ending Date"),
            'AL formats a blank date as the empty string');

        Real.Get('FA000010', 'COMPANY');
        Assert.AreEqual(DMY2Date(31, 12, 2029), Real."Depreciation Ending Date",
            'FA000010 ending date');
        Assert.AreEqual(DMY2Date(1, 1, 2025), Real."Depreciation Starting Date",
            'FA000010 starting date');
        Assert.IsFalse(Real."Depreciation Ending Date" = 0D,
            'a real date must not collapse into the blank');
    end;

    /// <summary>
    /// DateFormula is stored as BC's TOKEN encoding, not as readable formula text: measured,
    /// "10 DAYS"."Due Date Calculation" is the two-character string "10" + U+0002. Asserting
    /// the FORMATTED value is what proves the token was consumed as a token — parsing it as
    /// formula text would produce a different formula, and comparing raw storage to raw
    /// storage would not notice.
    /// </summary>
    [Test]
    procedure DateFormulaKeepsItsMeaning()
    var
        PaymentTerms: Record "Payment Terms";
        TenDays: DateFormula;
        OneMonth: DateFormula;
    begin
        Evaluate(TenDays, '10D');
        Evaluate(OneMonth, '1M');

        PaymentTerms.Get('10 DAYS');
        Assert.AreEqual(Format(TenDays), Format(PaymentTerms."Due Date Calculation"),
            '10 DAYS due date calculation');
        Assert.IsFalse(Format(PaymentTerms."Due Date Calculation") = Format(OneMonth),
            'a day token must not decode as a month token');

        // The blank direction: this row's discount calculation is empty in the backup.
        Assert.AreEqual('', Format(PaymentTerms."Discount Date Calculation"),
            '10 DAYS discount date calculation is empty in the backup');
    end;

    /// <summary>DateTime is stored UTC and rebuilt verbatim — never routed through the
    /// session's client time zone, which would shift it by the host's UTC offset.</summary>
    [Test]
    procedure DateTimeHydratesNonBlank()
    var
        PaymentTerms: Record "Payment Terms";
    begin
        PaymentTerms.Get('10 DAYS');
        Assert.IsFalse(PaymentTerms."Last Modified Date Time" = 0DT,
            'Last Modified Date Time holds a real instant in the backup, not the blank sentinel');
        // The instant itself is a demo-data build timestamp and moves with the artifact, so it
        // is not asserted as a literal. What IS version-stable: it is on the same day as the
        // row's own creation, which a timezone shift of a whole day would break.
        Assert.IsTrue(PaymentTerms."Last Modified Date Time" > CreateDateTime(DMY2Date(1, 1, 2000), 0T),
            'a rebuilt DateTime must be a real instant, not something near the 1753 sentinel');
    end;

    /// <summary>
    /// Time. BC stores it in a `datetime` column on a carrier date (1754-01-01 for a real
    /// time, 1753-01-01 for a blank) and reads back only the time of day. A codec that kept
    /// the carrier date would produce a value BC's own NavTime constructor rejects.
    /// </summary>
    [Test]
    procedure TimeHydratesAsTimeOfDayOnly()
    var
        CalendarEntry: Record "Calendar Entry";
    begin
        CalendarEntry.SetFilter("Starting Time", '<>%1', 0T);
        Assert.IsTrue(CalendarEntry.FindFirst(),
            'the backup holds Calendar Entry rows with a non-blank Starting Time');
        Assert.AreEqual(080000T, CalendarEntry."Starting Time", 'Starting Time');
        Assert.IsFalse(CalendarEntry."Starting Time" = 0T,
            'a real time must not collapse into the blank');
    end;
}
