// TestDataDateValueHydrationTests — the proving tests for issue #2259: rebuilding AL
// Date / DateTime / Time / DateFormula values from a BC backup.
//
// WHAT IS PROVED HERE, AND WHY IT IS HERE
//   These are claims about OUR OWN codec — that RecordPatches.ConvertTestDataValue mirrors
//   BC's SQL-cell-to-NavValue conversion (NavSqlCommand.CreateNavValueFromReader) for the
//   four types #2258 refused. They are not statements about what Business Central does with
//   AL source, so .claude/rules/bc-behavior-tests-go-upstream.md does not send them upstream;
//   the BC-behaviour half (a blank date read back as 0D on a real service tier) is the
//   separate corpus test named in the PR.
//
//   They are also deliberately NOT driven off the shipped 900 MB demo backup. CI has no
//   backup and no reader binary, and demo-data values are not guaranteed identical across the
//   eight BC versions in the matrix (27.0 through 28.4), so a test asserting "CRONUS's Start
//   Date is 2026-04-01" would be version-fragile in a way the codec is not. Every input below
//   is a JSON literal written here, of the exact shape measured from the reader against
//   sandbox/28.1.49838.50621's BusinessCentral-W1.bak — so the assertions are version-stable
//   while still being the real wire format.
//
// THE ONE THAT CAN SILENTLY GO WRONG
//   BC stores AL's blank date 0D as the SQL sentinel 1753-01-01 (SQL `datetime` cannot go
//   below 1753 and BC's own write path throws for any real date before 1754-01-01, so the
//   sentinel is unambiguous). A codec that passed 1753-01-01 through as a literal date would
//   hydrate every blank date cell as a date in 1753 — 14,412 cells in the shipped CRONUS
//   data — and every `if X = 0D` in AL would take the wrong branch, with no error anywhere.
//   BlankDate_... and BlankDateTime_... assert against that directly.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataDateValueHydrationTests
{
    /// <summary>The only thing the four date/time branches read off the field: its NCL type.
    /// A hand-built stand-in keeps these tests free of a booted engine or a metadata cache —
    /// the conversion under test is pure over (NclType, JSON value).</summary>
    private sealed class ValueMetadata : INavValueMetadata
    {
        internal ValueMetadata(NavNclType nclType, NavType navType)
        {
            NclType = nclType;
            NavType = navType;
        }

        public NavType NavType { get; }
        public NavNclType NclType { get; }
        public int NavDefinedLengthMetadata => 0;
        public NCLOptionMetadata NavOptionMetadata => null!;
    }

    private static readonly ValueMetadata DateField = new(NavNclType.NavDate, NavType.Date);
    private static readonly ValueMetadata DateTimeField = new(NavNclType.NavDateTime, NavType.DateTime);
    private static readonly ValueMetadata TimeField = new(NavNclType.NavTime, NavType.Time);
    private static readonly ValueMetadata FormulaField = new(NavNclType.NavDateFormula, NavType.DateFormula);

    private static NavValue Convert(INavValueMetadata metadata, string rawJsonValue)
    {
        using var doc = JsonDocument.Parse(rawJsonValue);
        return RecordPatches.ConvertTestDataValue(
            metadata, doc.RootElement.Clone(), 312, "Purchases & Payables Setup", 46,
            "Allow Document Deletion Before");
    }

    private static TestDataHydrationRefusal Refusal(INavValueMetadata metadata, string rawJsonValue)
        => Assert.Throws<TestDataHydrationRefusal>(() => Convert(metadata, rawJsonValue));

    // ------------------------------------------------------------------ Date --

    [Fact]
    public void BlankDate_HydratesAsAlsBlankDate_NotAsALiteral1753()
    {
        // The exact cell that currently refuses the whole `Purchases & Payables Setup` table:
        // field 46 "Allow Document Deletion Before", SQL `datetime NOT NULL`, holding the
        // sentinel. The column cannot be NULL, so a sentinel is the only option BC has.
        var value = Assert.IsType<NavDate>(Convert(DateField, "\"1753-01-01 00:00:00.000\""));

        // AL's 0D is DateTime.MinValue (0001-01-01), which is what
        // NavDateTimeValue.IsZeroOrEmpty tests against — the same question AL's `if X = 0D`
        // asks.
        Assert.True(value.IsZeroOrEmpty);
        Assert.Equal(default, value.Value);
        Assert.Same(NavDate.Undefined, value);

        // Stated as its own assertion because it is the defect, not a corollary: a codec that
        // handed the sentinel to NavDate.Create verbatim would produce a 1753 date here and
        // every blank-date branch in AL would flip.
        Assert.NotEqual(new DateTime(1753, 1, 1), value.Value);
    }

    [Fact]
    public void RealDate_HydratesAsThatDate_WithBcsOwnLocalKind()
    {
        // Measured shape: Account Schedules Chart Setup."Start Date" = 2026-04-01.
        var value = Assert.IsType<NavDate>(Convert(DateField, "\"2026-04-01 00:00:00.000\""));

        Assert.False(value.IsZeroOrEmpty);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Local), value.Value);
        // NavDate's constructor REJECTS anything that is not DateTimeKind.Local, so getting
        // the kind wrong is a throw rather than a wrong answer — but pin it anyway, because
        // the kind is what BC's own read path spells out and a future edit could "tidy" it.
        Assert.Equal(DateTimeKind.Local, value.Value.Kind);
    }

    [Fact]
    public void DateBeforeTheSqlFloor_IsRefused_NotSilentlyClamped()
    {
        // BC's write path throws NavCSideException for any real date below 1754-01-01, so such
        // a value cannot have come from BC. Refusing names the table and column; clamping it
        // to the sentinel would turn an impossible value into a plausible blank.
        var ex = Refusal(DateField, "\"1600-05-04 00:00:00.000\"");
        Assert.Contains("Purchases & Payables Setup", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Allow Document Deletion Before", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- DateTime --

    [Fact]
    public void BlankDateTime_HydratesAsAlsBlankDateTime_NotAsALiteral1753()
    {
        // Measured: Acc. Sched. KPI Web Srv. Setup."Data Last Updated" holds the sentinel.
        // Note the constant is a DIFFERENT one from NavDate's — same instant, DateTimeKind.Utc
        // rather than Local — which is why the two cases are not collapsed into one.
        var value = Assert.IsType<NavDateTime>(Convert(DateTimeField, "\"1753-01-01 00:00:00.000\""));

        Assert.True(value.IsZeroOrEmpty);
        Assert.Equal(default, value.Value);
        Assert.Same(NavDateTime.Undefined, value);
        Assert.NotEqual(new DateTime(1753, 1, 1), value.Value);
    }

    [Fact]
    public void RealDateTime_IsStoredVerbatimAsUtc_NotTimezoneConverted()
    {
        // Measured: Payment Terms."Last Modified Date Time" = 2026-05-19 23:24:22.663.
        //
        // THE TRAP #2259 flagged, and what this assertion does and does not catch — measured on
        // a UTC+2 host, not reasoned:
        //
        //   correct    SpecifyKind(Utc)   + Server + unspecifiedAsLocal:false -> 23:24:22.663Z
        //   Local kind SpecifyKind(Local) + Server + unspecifiedAsLocal:false -> 21:24:22.663Z
        //   no kind    Unspecified        + Server + unspecifiedAsLocal:false -> throws
        //   CreateFromObject's shape (Client + unspecifiedAsLocal:true)       -> 23:24:22.663Z
        //
        // Only the correct branch skips ConvertToUTc entirely: NavDateTime's constructor stores
        // a Utc-kind value verbatim and never touches a session or a zone. So this assertion
        // catches the Local-kind slip on any host with a non-zero UTC offset, and catches the
        // no-kind slip everywhere (it throws).
        //
        // It does NOT catch CreateFromObject's shape here, and that is worth being exact about
        // rather than claiming otherwise: ConvertToUTc asks the SESSION for the client zone, and
        // a session-free unit test gets UTC back, so the shift only appears in a real run whose
        // session carries a non-UTC zone. On a UTC host (GitHub's runners) none of the variants
        // differ at all — there is genuinely nothing to observe. The authority for this branch is
        // the decompiled read path quoted in ConvertTestDataValue, not this test alone.
        var value = Assert.IsType<NavDateTime>(Convert(DateTimeField, "\"2026-05-19 23:24:22.663\""));

        Assert.False(value.IsZeroOrEmpty);
        Assert.Equal(new DateTime(2026, 5, 19, 23, 24, 22, 663, DateTimeKind.Utc), value.Value);
        Assert.Equal(DateTimeKind.Utc, value.Value.Kind);
    }

    // ------------------------------------------------------------------ Time --

    [Fact]
    public void BlankTime_HydratesAsAlsBlankTime_NotAsMidnight()
    {
        // NavTime carries its OWN sentinel, NavTime.SqlTimeUndefined. It is the same instant as
        // NavDate's, but a blank Time and 00:00:00 are different AL values, so the sentinel
        // check is what keeps them apart: real midnight is stored as 1754-01-01 00:00:00.
        var value = Assert.IsType<NavTime>(Convert(TimeField, "\"1753-01-01 00:00:00.000\""));

        Assert.True(value.IsZeroOrEmpty);
        Assert.Same(NavTime.Undefined, value);

        var midnight = Assert.IsType<NavTime>(Convert(TimeField, "\"1754-01-01 00:00:00.000\""));
        Assert.False(midnight.IsZeroOrEmpty);
        Assert.NotSame(NavTime.Undefined, midnight);
    }

    [Fact]
    public void RealTime_HydratesAsThatTimeOfDay_IgnoringTheCarrierDate()
    {
        // Measured: Calendar Entry."Starting Time" = 1754-01-01 08:00:00.000. BC reads only the
        // hour/minute/second/millisecond off the cell; the 1754-01-01 date part is SQL's
        // carrier for a `datetime` column and is not part of the AL value.
        var value = Assert.IsType<NavTime>(Convert(TimeField, "\"1754-01-01 08:00:00.000\""));

        Assert.False(value.IsZeroOrEmpty);
        Assert.Equal(TimeSpan.FromHours(8), value.Value.TimeOfDay);
        // NavTime pins every time onto BC's own carrier day (0001-01-02), so a codec that kept
        // the SQL carrier date would produce a value NavTime's constructor rejects outright.
        Assert.Equal(new DateTime(1, 1, 2), value.Value.Date);
    }

    // ----------------------------------------------------------- DateFormula --

    [Fact]
    public void DateFormula_KeepsBcsTokenEncoding_RatherThanParsingItAsFormulaText()
    {
        // Measured: Payment Terms."Due Date Calculation" for code "10 DAYS" is the two-token
        // string "10" followed by U+0002, not the readable "10D". Handing that to the string
        // overload (isTokenString: false) would run it through NavDateFormulaEvaluator.Parse as
        // formula TEXT and produce a different value — that is what #2259 flagged, and
        // isTokenString: true is its answer.
        var value = Assert.IsType<NavDateFormula>(Convert(FormulaField, "\"10\\u0002\""));

        Assert.False(value.IsZeroOrEmpty);
        Assert.Equal("10", value.TokenString);
        Assert.Equal("10", value.Value);

        // A month token is a different control byte, so the two are not interchangeable.
        var oneMonth = Assert.IsType<NavDateFormula>(Convert(FormulaField, "\"1\\u0005\""));
        Assert.Equal("1", oneMonth.TokenString);
        Assert.NotEqual(value.TokenString, oneMonth.TokenString);
    }

    [Fact]
    public void EmptyDateFormula_HydratesAsTheFieldsBlankValue()
    {
        var value = Assert.IsType<NavDateFormula>(Convert(FormulaField, "\"\""));

        Assert.True(value.IsZeroOrEmpty);
        Assert.Equal("", value.TokenString);
        // The same instance BC's own field.EmptyValue resolves to for a DateFormula field
        // (NCLMetaField.EmptyValue -> NavValue.GetDefaultNavValue -> NavDateFormula.Default).
        Assert.Same(NavDateFormula.Default, value);
    }

    // ------------------------------------------------ refusal still works --

    [Fact]
    public void AValueThatIsNotTheMeasuredWireShape_RefusesTheTableRatherThanGuessing()
    {
        // Four reasons to refuse were removed; the ABILITY to refuse was not. An unparseable
        // cell still aborts the whole table, naming it, rather than substituting a default.
        foreach (var metadata in new INavValueMetadata[] { DateField, DateTimeField, TimeField })
        {
            var ex = Refusal(metadata, "\"not a timestamp\"");
            Assert.Contains("Purchases & Payables Setup", ex.Message, StringComparison.Ordinal);
            Assert.Contains("not a timestamp", ex.Message, StringComparison.Ordinal);

            // A JSON number is not the reader's shape for any of them either.
            Assert.Throws<TestDataHydrationRefusal>(() => Convert(metadata, "17530101"));
        }

        // And a DateFormula's token string is a STRING; a number would be a decoding change
        // this codec must not paper over.
        Assert.Throws<TestDataHydrationRefusal>(() => Convert(FormulaField, "5"));
    }

    [Fact]
    public void TypesThisBuildStillCannotRebuild_KeepRefusing()
    {
        // #2245 tracks Blob/Media/MediaSet. RecordId is a 448-byte structure with no textual
        // wire form. None of them acquired a branch here, and the refusal must name the type
        // so the reason reaches whoever reads the run's output.
        foreach (var nclType in new[]
                 { NavNclType.NavBlob, NavNclType.NavMedia, NavNclType.NavMediaSet, NavNclType.NavRecordId })
        {
            var ex = Refusal(new ValueMetadata(nclType, NavType.BLOB), "\"1753-01-01 00:00:00.000\"");
            Assert.Contains(nclType.ToString(), ex.Message, StringComparison.Ordinal);
        }
    }
}
