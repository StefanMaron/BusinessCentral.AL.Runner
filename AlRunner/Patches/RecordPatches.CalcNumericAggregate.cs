// RecordPatches.CalcNumericAggregate — TempTableDataProvider.CalcNumeric: the Sum/Count/Min/Max
// a FlowField's CalcFormula asks the provider for.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;
public static partial class RecordPatches
{
    /// <summary>
    /// Replacement for TempTableDataProvider.CalcNumeric(CalcNumericProviderRequest).
    /// The real override throws NotSupportedException; this replacement iterates in-memory rows
    /// via the private Filter() helper and aggregates each requested FlowField.
    /// <para>#2937 — the doc comment here used to claim count/sum/average were "the only three
    /// calculation methods routed through CalcNumeric", and the result switch ended in
    /// <c>_ =&gt; sums[j]</c>, so Min/Max/Lookup/Exists/None were all answered with the SUM
    /// accumulator. For Min/Max nothing ever wrote that accumulator, so they came back as a
    /// constant 0 whatever the data — right for an empty source set only by coincidence.
    /// Count/Sum/Average is indeed what BC ROUTES here (DistinctSourceTable.AddField buckets
    /// Min/Max into MinMaxFlowFields → CalcMinMax, Lookup and Exists into their own lists), but
    /// "BC does not send it" is not a reason to answer it wrongly: Min/Max are now aggregated
    /// properly and everything else throws. Aggregation itself lives in
    /// <see cref="ComputeCalcNumericAggregate"/>.</para>
    /// <para>NegateResult (<c>CalcFormula = -sum(...)</c>) is applied here because BC applies it
    /// at this level too: NavSqlAggregateCommand's aggregate reader negates each aggregated
    /// value inside the provider, before the FieldDictionary is returned. The negation itself is
    /// BC's own NCLMetaCalculationFormula.NegateValue, reached through
    /// <see cref="FlowFieldPatches.NegateAggregateResult"/> so the two runner paths that negate
    /// a FlowField aggregate share one implementation (#1708, #2323).</para>
    /// <para>REACHABILITY, measured rather than assumed (#2937 left this open): instrumenting
    /// this method's result loop and running the whole al-language corpus (2496 tests) plus all
    /// of tests/runner-extras (264 tests) produced ZERO hits. FlowFieldPatches hooks
    /// NavRecord.CalcFieldsAsync ahead of FlowFieldsHelper, so ordinary CalcFields — on a
    /// temporary record too, since every runner table is backed by TempTableDataProvider — never
    /// arrives here. That is why the coverage for this method is C# (CalcNumericAggregateTests)
    /// and not AL: there is no AL shape today that reaches it, so an AL test would pass without
    /// executing a line of it.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object TempTableDataProvider_CalcNumeric(object self, object request)
    {
        // No Date (2000000007) materialisation here since #3506: BC's own DateDataProvider
        // answers that table, so a Date read never reaches a TempTableDataProvider.

        var rt = request.GetType();
        var companyToken   = (int)BcShape.Property(rt, "CompanyToken", "AL record data access").GetValue(request)!;
        var filtersAndMarks = BcShape.Property(rt, "FiltersAndMarks", "AL record data access").GetValue(request);
        var sourceTable    = (NCLMetaTable)BcShape.Property(
            rt, "MetaApplicationObject", "AL record data access").GetValue(request)!;
        var fieldsToCalc   = (FieldList)BcShape.Property(
            rt, "FieldsToCalculate", "AL record data access").GetValue(request)!;
        int fieldCount     = fieldsToCalc.Count;

        var primaryKeySortingFields = _fTtdpPrimaryKeySortingFields!.GetValue(self);
        int recordCount = 0;

        // Per-field state resolved once, not once per row: the calculation method, the source
        // field the formula names (Count has none), and the source values collected across the
        // matching rows. Only the value-consuming methods get a list — a Count field's slot
        // stays null and nothing is collected for it.
        //
        // Collecting the values rather than folding them into a running total is the same shape
        // RecordPatches.QueryProjection's ComputeAggregateCore uses, and it is what lets one
        // helper own Sum/Average/Min/Max instead of the result switch reading whichever
        // accumulator happened to be written. The cost is one NavValue REFERENCE per matching
        // row per aggregated field — the rows' own value objects, not copies, and the rows are
        // already materialised in the temp store by the Filter() call below.
        var methods = new NCLMetaCalculationMethod[fieldCount];
        var sourceFields = new NCLMetaField?[fieldCount];
        var sourceValues = new List<NavValue?>?[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            var field = (NCLMetaField)fieldsToCalc[i];
            methods[i] = field.CalculationFormula.CalculationMethod;
            if (methods[i] is NCLMetaCalculationMethod.Sum or NCLMetaCalculationMethod.Average
                or NCLMetaCalculationMethod.Min or NCLMetaCalculationMethod.Max)
            {
                sourceFields[i] = sourceTable.GetFieldByNo(field.CalculationFormula.FieldId, trapError: true);
                // Source field unresolvable → no values are collected and the aggregate answers
                // its empty-set value, which is what this method did before #2937 and what
                // FlowFieldPatches' own srcFieldColumn < 0 arm does. Left as-is deliberately:
                // making it loud is a change to the shared FlowField behaviour, not to this
                // method, and belongs with the sibling rather than half-applied here.
                if (sourceFields[i] != null) sourceValues[i] = new List<NavValue?>();
            }
        }

        var rows = (System.Collections.IEnumerable)_mTtdpFilter!.Invoke(
            self, new object?[] { companyToken, filtersAndMarks, null, primaryKeySortingFields, false })!;

        foreach (TempTableRecordBuffer row in rows)
        {
            checked { recordCount++; }
            for (int i = 0; i < fieldCount; i++)
            {
                var values = sourceValues[i];
                if (values == null) continue;
                values.Add(row[sourceFields[i]!.ColumnIndex]);
            }
        }

        var tuples = new Tuple<INavFieldMetadata, NavValue>[fieldCount];
        for (int j = 0; j < fieldCount; j++)
        {
            var field = (NCLMetaField)fieldsToCalc[j];
            var formula = field.CalculationFormula;

            // BC negates at this level too — see the method's doc comment. The negation is
            // passed IN rather than applied after the call so that "aggregate, then negate iff
            // NegateResult" is one decision with one owner, and so a test can drive it: BC's
            // own NCLMetaCalculationFormula.NegateValue resolves SourceField through the
            // metadata registry and therefore needs a live session, which a unit test does not
            // have. Exists never reaches the negation (ComputeCalcNumericAggregate throws for
            // it first), so the #2323 exist carve-out in FlowFieldPatches has no counterpart to
            // make here.
            var navValue = ComputeCalcNumericAggregate(
                methods[j], field, recordCount,
                (IEnumerable<NavValue?>?)sourceValues[j] ?? Array.Empty<NavValue?>(),
                $"{field.Parent?.TableName}.{field.FieldName}",
                formula.NegateResult,
                v => FlowFieldPatches.NegateAggregateResult(
                    formula, v, "TempTableDataProvider.CalcNumeric"));

            tuples[j] = new Tuple<INavFieldMetadata, NavValue>(field, navValue);
        }

        return _ctorFieldDictionaryNavValue!.Invoke(new object[] { tuples })!;
    }

    /// <summary>
    /// One CalcNumeric field's aggregate, over the source values of the rows that matched the
    /// formula's filters (<paramref name="rowCount"/> is how many rows matched — Count's answer,
    /// and Average's divisor, both of which count rows rather than non-null values).
    /// <para>Min/Max reuse <see cref="FlowFieldPatches.NavValueCompare"/> and
    /// <see cref="FlowFieldPatches.TypedDefaultForField"/> rather than re-deriving comparison or
    /// default semantics — the same reuse RecordPatches.QueryProjection's ComputeAggregateCore
    /// makes, so all three of the runner's aggregate paths order values and answer an empty set
    /// identically.</para>
    /// <para>The empty-source-set answers are deliberate, not a fallthrough: corpus PR
    /// StefanMaron/BusinessCentral.AL.Language.Tests#171 measured real BC on eight service tiers
    /// answering 0 for min/max/average over no matching rows — and 0D for a Date-typed one,
    /// which is why the answer is the field's OWN typed default and not a numeric zero.</para>
    /// <para>Anything else throws. BC never routes Exists/Lookup/None through CalcNumeric
    /// (DistinctSourceTable.AddField buckets them into their own field lists), so one arriving
    /// here means the dispatch changed — and per loud-failures.md that has to be loud rather
    /// than answered with a default. Min and Max are answered even though BC does not route
    /// them here either, because the aggregation is exactly the same work and a wrong constant
    /// was the #2937 defect.</para>
    /// </summary>
    /// <param name="negateResult">the formula's <c>NegateResult</c> — the leading minus in
    /// <c>CalcFormula = -sum(...)</c> (#1708).</param>
    /// <param name="negate">applies that minus, and is only ever called when
    /// <paramref name="negateResult"/> is true. Required rather than optional: a null default
    /// would let a caller silently answer the POSITIVE aggregate for a negated formula, which
    /// is the exact silent wrong value #1708 is about. Production passes
    /// <see cref="FlowFieldPatches.NegateAggregateResult"/>, so BC's own
    /// NCLMetaCalculationFormula.NegateValue stays the single owner of the negation.</param>
    internal static NavValue ComputeCalcNumericAggregate(
        NCLMetaCalculationMethod method,
        INavValueMetadata resultMetadata,
        int rowCount,
        IEnumerable<NavValue?> sourceValues,
        string fieldDescription,
        bool negateResult,
        Func<NavValue, NavValue> negate)
    {
        if (negateResult && negate == null)
            throw new ArgumentNullException(nameof(negate),
                $"NegateResult is set on '{fieldDescription}' but no negation was supplied — "
                + "answering the positive aggregate would be the silent wrong value of #1708");

        var aggregate = ComputeCalcNumericAggregateCore(
            method, resultMetadata, rowCount, sourceValues, fieldDescription);
        return negateResult ? negate(aggregate) : aggregate;
    }

    private static NavValue ComputeCalcNumericAggregateCore(
        NCLMetaCalculationMethod method,
        INavValueMetadata resultMetadata,
        int rowCount,
        IEnumerable<NavValue?> sourceValues,
        string fieldDescription)
    {
        switch (method)
        {
            case NCLMetaCalculationMethod.Count:
                return NavValue.CreateNavValueFromObject(resultMetadata, rowCount);

            case NCLMetaCalculationMethod.Sum:
            case NCLMetaCalculationMethod.Average:
            {
                Decimal18 sum = default;
                foreach (var v in sourceValues)
                {
                    if (v == null) continue;
                    sum = checked(sum + v.ToDecimal());
                }
                if (method == NCLMetaCalculationMethod.Sum)
                    // An empty sum is 0 by arithmetic, not by accumulator accident.
                    return NavValue.CreateNavValueFromObject(resultMetadata, sum);
                return rowCount > 0
                    ? NavValue.CreateNavValueFromObject(resultMetadata, sum / rowCount)
                    : EmptyAggregateDefault(resultMetadata);
            }

            case NCLMetaCalculationMethod.Min:
            case NCLMetaCalculationMethod.Max:
            {
                NavValue? best = null;
                foreach (var v in sourceValues)
                {
                    if (v == null) continue;
                    if (best == null
                        || (method == NCLMetaCalculationMethod.Min && FlowFieldPatches.NavValueCompare(v, best) < 0)
                        || (method == NCLMetaCalculationMethod.Max && FlowFieldPatches.NavValueCompare(v, best) > 0))
                        best = v;
                }
                return best ?? EmptyAggregateDefault(resultMetadata);
            }

            default:
                throw new RunnerOutOfScopeException(
                    "TempTableDataProvider.CalcNumeric",
                    $"not-yet-implemented — CalculationMethod {method} on '{fieldDescription}' "
                    + "is not aggregated by CalcNumeric. BC routes Exists to CalcExists and "
                    + "Lookup to CalcLookup (DistinctSourceTable.AddField), so a CalcNumeric "
                    + "request carrying one means the dispatch changed; answering it with the "
                    + "sum accumulator was issue #2937",
                    "todo");
        }
    }

    /// <summary>
    /// What an aggregate answers when no row contributed a value: the result field's OWN typed
    /// default (0 for Decimal/Integer, 0D for Date, …), never a bare numeric literal — same
    /// chain FlowFieldPatches' Min/Max/Lookup arms use.
    /// </summary>
    private static NavValue EmptyAggregateDefault(INavValueMetadata resultMetadata)
        => FlowFieldPatches.TypedDefaultForField(resultMetadata)
           ?? NavValue.CreateNavValueFromObject(resultMetadata, 0);
}
