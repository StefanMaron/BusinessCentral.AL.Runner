// CalcNumericAggregateTests — issue #2937.
//
// RecordPatches.TempTableDataProvider_CalcNumeric is the Cecil replacement for BC's
// TempTableDataProvider.CalcNumeric (whose real body throws NotSupportedException). Before
// #2937 its result switch ended in `_ => sums[j]`: every CalculationMethod other than Count
// and a non-empty Average was answered with the SUM accumulator — which for Min/Max was never
// written to, so they came back as a constant 0 for any data, silently.
//
// These tests pin the aggregation itself, RecordPatches.ComputeCalcNumericAggregate, which is
// the seam the replacement's row loop feeds. They are runner-internal C# contract tests, not a
// claim about what BC does: the BC answers asserted here were measured on eight real service
// tiers (BC 27.0-28.4) by corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#171, run
// 33994147862, and are quoted per test. See bc-behavior-tests-go-upstream.md — the BC-behaviour
// claim lives upstream; what lives here is "the runner's aggregate helper answers those values".
//
// The metadata objects are real Ncl ones (NavValueMetadata.DefaultMetadata), not fakes, so
// NavValue.CreateNavValueFromObject and FlowFieldPatches.TypedDefaultForField take exactly the
// path they take in production.
using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

public sealed class CalcNumericAggregateTests
{
    private static INavValueMetadata DecimalMeta => NavValueMetadata.DefaultMetadata(NavNclType.NavDecimal);
    private static INavValueMetadata IntegerMeta => NavValueMetadata.DefaultMetadata(NavNclType.NavInteger);
    private static INavValueMetadata DateMeta => NavValueMetadata.DefaultMetadata(NavNclType.NavDate);

    private static NavValue?[] Decimals(params decimal[] values)
        => values.Select(v => (NavValue?)NavDecimal.Create((Decimal18)v)).ToArray();

    private static NavValue?[] Integers(params int[] values)
        => values.Select(v => (NavValue?)NavInteger.Create(v)).ToArray();

    // NavDate's ctor rejects anything whose Kind is not Local (NavNCLDateInvalidException),
    // which is BC's own invariant for a date value, not a test convenience.
    private static NavDate Date(int year, int month, int day)
        => NavDate.Create(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local));

    private static NavValue?[] Dates(params NavDate[] values)
        => values.Select(v => (NavValue?)v).ToArray();

    // Every unsigned aggregate goes through here, and supplies a negation that FAILS the test
    // if it is ever called — so the 12 tests below assert not only their value but that an
    // unsigned formula is never routed through the negation at all.
    private static NavValue Aggregate(
        NCLMetaCalculationMethod method, INavValueMetadata meta, NavValue?[] sourceValues, int? rowCount = null)
        => RecordPatches.ComputeCalcNumericAggregate(
            method, meta, rowCount ?? sourceValues.Length, sourceValues, "CFM Header.Test Field",
            negateResult: false,
            negate: _ => throw new InvalidOperationException(
                "negation applied to a formula whose NegateResult is false"));

    // The signed counterpart. `negate` stands in for BC's own
    // NCLMetaCalculationFormula.NegateValue, which CANNOT run in a unit test: it resolves
    // SourceField through the metadata registry, and without a live session that throws
    // PlatformNotSupportedException ("Windows Principal functionality is not supported on this
    // platform") — measured, not assumed. So these tests pin the WIRING (is the aggregate
    // negated, exactly when NegateResult says so), not BC's negation semantics. Those are
    // pinned upstream on eight real service tiers by the corpus's own CFS Tests (codeunit
    // 60912, TestCalcFormulaSignFilters*), which cover `CalcFormula = -sum(...)` end to end.
    //
    // The stand-in mirrors the Decimal arm of BC's method, quoted in FlowFieldPatches:
    //     NavType.Decimal => NavDecimal.Create(-((NavDecimal)value).Value)
    private static NavValue NegatedAggregate(
        NCLMetaCalculationMethod method, INavValueMetadata meta, NavValue?[] sourceValues, int? rowCount = null)
        => RecordPatches.ComputeCalcNumericAggregate(
            method, meta, rowCount ?? sourceValues.Length, sourceValues, "CFM Header.Test Field",
            negateResult: true,
            negate: v => NavDecimal.Create(-v.ToDecimal()));

    private static decimal AsDecimal(NavValue v) => (decimal)v.ToDecimal();

    // ── Populated aggregates: the values a real service tier answered ──────────────────
    // Corpus #171 seed D1: Amounts 40, -10, 75, 20 over four rows.
    // Record_CalcFields_Min_ReturnsSmallestSourceValue_IncludingNegatives -> -10
    [Fact]
    public void Min_DecimalSource_ReturnsSmallestIncludingNegatives()
        => Assert.Equal(-10m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Min, DecimalMeta, Decimals(40, -10, 75, 20))));

    // Record_CalcFields_Max_ReturnsLargestSourceValue -> 75
    [Fact]
    public void Max_DecimalSource_ReturnsLargest()
        => Assert.Equal(75m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Max, DecimalMeta, Decimals(40, -10, 75, 20))));

    // Same two tests, Integer source: Quantities 3, 4, 6, 8 -> 3 / 8.
    [Fact]
    public void MinMax_IntegerSource_ReturnsSmallestAndLargest()
    {
        var values = Integers(3, 4, 6, 8);
        Assert.Equal(3m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Min, IntegerMeta, values)));
        Assert.Equal(8m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Max, IntegerMeta, values)));
    }

    // Record_CalcFields_MinMax_ZeroValuedRowsParticipate — D4 is 10 plus three 0 rows:
    // min is 0 (the zeros are ordinary participants, not "missing"), max is 10.
    [Fact]
    public void MinMax_ZeroValuedRowsParticipate()
    {
        var values = Decimals(10, 0, 0, 0);
        Assert.Equal(0m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Min, DecimalMeta, values)));
        Assert.Equal(10m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Max, DecimalMeta, values)));
    }

    // Record_CalcFields_MinMax_DateSource_ReturnFirstAndLastDate — earliest / latest.
    [Fact]
    public void MinMax_DateSource_ReturnsEarliestAndLatest()
    {
        var values = Dates(Date(2026, 3, 4), Date(2025, 1, 2), Date(2026, 12, 31));
        Assert.Equal(Date(2025, 1, 2), Aggregate(NCLMetaCalculationMethod.Min, DateMeta, values));
        Assert.Equal(Date(2026, 12, 31), Aggregate(NCLMetaCalculationMethod.Max, DateMeta, values));
    }

    // Sum / Average / Count over the same D1 rows. Average divides by every matching row
    // (Record_CalcFields_Average_DividesByEveryMatchingRow), so D4's 10+0+0+0 is 2.5.
    [Fact]
    public void SumAverageCount_MatchTheMeasuredValues()
    {
        var d1 = Decimals(40, -10, 75, 20);
        Assert.Equal(125m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Sum, DecimalMeta, d1)));
        Assert.Equal(31.25m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Average, DecimalMeta, d1)));
        Assert.Equal(4m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Count, IntegerMeta, d1)));
        Assert.Equal(2.5m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Average, DecimalMeta, Decimals(10, 0, 0, 0))));
    }

    // ── The empty source set: deliberate, not a stale accumulator ──────────────────────
    // Record_CalcFields_MinMaxAverage_NoMatchingRows_ReturnZero: BC answers 0 and CalcFields
    // still returns true. The pre-fix code was right here only by coincidence — the sum
    // accumulator it returned had never been written. Assert the TYPED default, which is what
    // distinguishes "recognised the empty set" from "read an unwritten accumulator": a Date
    // aggregate must answer 0D, and a decimal accumulator cannot produce a date at all.
    [Fact]
    public void EmptySourceSet_DecimalAggregates_AnswerZero()
    {
        var none = Array.Empty<NavValue?>();
        Assert.Equal(0m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Min, DecimalMeta, none)));
        Assert.Equal(0m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Max, DecimalMeta, none)));
        Assert.Equal(0m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Average, DecimalMeta, none)));
        Assert.Equal(0m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Sum, DecimalMeta, none)));
        Assert.Equal(0m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Count, IntegerMeta, none)));
    }

    [Fact]
    public void EmptySourceSet_DateAggregates_AnswerZeroDate()
    {
        var none = Array.Empty<NavValue?>();
        Assert.Equal(NavDate.Default, Aggregate(NCLMetaCalculationMethod.Min, DateMeta, none));
        Assert.Equal(NavDate.Default, Aggregate(NCLMetaCalculationMethod.Max, DateMeta, none));
    }

    // A row whose source column is unset contributes nothing, and does not become a spurious
    // minimum of 0 — the same "null value is skipped" rule ComputeAggregateCore applies.
    [Fact]
    public void NullSourceValues_AreSkipped_NotTreatedAsZero()
    {
        var values = new NavValue?[] { null, NavDecimal.Create((Decimal18)5m), null, NavDecimal.Create((Decimal18)3m) };
        Assert.Equal(3m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Min, DecimalMeta, values)));
        Assert.Equal(5m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Max, DecimalMeta, values)));
        Assert.Equal(8m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Sum, DecimalMeta, values)));
        // Average divides by the ROW count BC counted, not by the non-null values (4 rows).
        Assert.Equal(2m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Average, DecimalMeta, values)));
    }

    // ── Negative: a method CalcNumeric cannot answer must throw, never default ──────────
    // BC's DistinctSourceTable.AddField never puts an Exists/Lookup field in the
    // NumericFlowFields list CalcNumericAsync is called with, so one arriving here means the
    // dispatch changed. loud-failures.md: name the surface, do not answer 0.
    [Theory]
    [InlineData(NCLMetaCalculationMethod.Exists)]
    [InlineData(NCLMetaCalculationMethod.Lookup)]
    [InlineData(NCLMetaCalculationMethod.None)]
    public void UnsupportedCalculationMethod_Throws_NamingSurfaceAndMethod(NCLMetaCalculationMethod method)
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => Aggregate(method, DecimalMeta, Decimals(40, -10, 75, 20)));
        Assert.Equal("TempTableDataProvider.CalcNumeric", ex.Api);
        Assert.Contains(method.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("CFM Header.Test Field", ex.Message, StringComparison.Ordinal);
    }

    // ── NegateResult: CalcFormula = -sum(...) at the provider level (#1708, #2937) ──────
    // BC applies the leading minus inside the provider — NavSqlAggregateCommand's aggregate
    // reader negates every aggregated FlowField value whose formula has NegateResult, before
    // the FieldDictionary goes back to FlowFieldsHelper. TempTableDataProvider_CalcNumeric is
    // the runner's stand-in for that provider, and before this change it dropped the sign:
    // a `-sum(...)` came back POSITIVE. Each assertion below is a value the pre-fix code got
    // wrong, not merely a value it did not compute.

    // The headline case: the same D1 rows that sum to +125 must answer -125 when signed.
    [Fact]
    public void Sum_NegateResult_ReturnsTheNegatedAggregate()
    {
        var d1 = Decimals(40, -10, 75, 20);
        Assert.Equal(125m, AsDecimal(Aggregate(NCLMetaCalculationMethod.Sum, DecimalMeta, d1)));
        Assert.Equal(-125m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Sum, DecimalMeta, d1)));
    }

    // Negation composes with the Min/Max aggregation this issue added, rather than being
    // applied to the never-written sum accumulator the old catch-all arm returned.
    [Fact]
    public void MinMaxAverageCount_NegateResult_NegateTheirOwnAggregate()
    {
        var d1 = Decimals(40, -10, 75, 20);
        Assert.Equal(10m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Min, DecimalMeta, d1)));
        Assert.Equal(-75m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Max, DecimalMeta, d1)));
        Assert.Equal(-31.25m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Average, DecimalMeta, d1)));
        Assert.Equal(-4m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Count, IntegerMeta, d1)));
    }

    // An empty source set answers the typed default, and -0 is still 0 — the sign must not
    // turn "no matching rows" into something else.
    [Fact]
    public void EmptySourceSet_NegateResult_StillAnswersZero()
    {
        var none = Array.Empty<NavValue?>();
        Assert.Equal(0m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Sum, DecimalMeta, none)));
        Assert.Equal(0m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Min, DecimalMeta, none)));
        Assert.Equal(0m, AsDecimal(NegatedAggregate(NCLMetaCalculationMethod.Max, DecimalMeta, none)));
    }

    // Negative case: a signed formula with no negation supplied must throw rather than hand
    // back the POSITIVE aggregate — that silent wrong value is exactly issue #1708.
    [Fact]
    public void NegateResult_WithoutANegation_Throws_RatherThanAnsweringPositive()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => RecordPatches.ComputeCalcNumericAggregate(
                NCLMetaCalculationMethod.Sum, DecimalMeta, 4, Decimals(40, -10, 75, 20),
                "CFM Header.Test Field", negateResult: true, negate: null!));
        Assert.Contains("#1708", ex.Message, StringComparison.Ordinal);
    }

    // #2323: exist FlowFields must never reach the negation, because BC's NegateValue switches
    // on the SOURCE field's type and would mis-handle a boolean. Here that is structural — the
    // unsupported-method throw happens first — and the negation asserts it is never called.
    [Theory]
    [InlineData(NCLMetaCalculationMethod.Exists)]
    [InlineData(NCLMetaCalculationMethod.Lookup)]
    public void UnsupportedCalculationMethod_WithNegateResult_ThrowsBeforeNegating(
        NCLMetaCalculationMethod method)
    {
        Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.ComputeCalcNumericAggregate(
                method, DecimalMeta, 4, Decimals(40, -10, 75, 20), "CFM Header.Test Field",
                negateResult: true,
                negate: _ => throw new InvalidOperationException(
                    "negation reached for a method CalcNumeric cannot aggregate")));
    }
}
