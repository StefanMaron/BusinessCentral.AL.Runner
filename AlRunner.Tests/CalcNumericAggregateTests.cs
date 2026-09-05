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

    private static NavValue Aggregate(
        NCLMetaCalculationMethod method, INavValueMetadata meta, NavValue?[] sourceValues, int? rowCount = null)
        => RecordPatches.ComputeCalcNumericAggregate(
            method, meta, rowCount ?? sourceValues.Length, sourceValues, "CFM Header.Test Field");

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
}
