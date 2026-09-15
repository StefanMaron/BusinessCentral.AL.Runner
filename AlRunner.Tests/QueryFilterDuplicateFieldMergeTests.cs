// Issue #3492 — the runner's query filter push-down must fold two conditions on one field
// into one dictionary entry, because BC's FilterFieldDictionary cannot hold the field twice.
//
// RecordPatches.TranslateQueryFilters collects its tuples in three independent passes (runtime
// SetRange/SetFilter on a column, a static ColumnFilter, and the dataitem's own
// DataItemTableFilter), each retargeted to the column's SOURCE TABLE FIELD. Two passes can
// therefore name one field — and so can one pass on its own, when two query columns of a join
// read the same source field. Microsoft's query 7314 CalcRsvQtyOnPicksShipsWithIT does exactly
// that: filter(Positive) on the outer dataitem and filter(Positive_2) on the inner, both
// Reservation Entry field 28.
//
// BC's own KeyValueSortedDictionary builds its key lookup LAZILY, with Enumerable.ToDictionary
// over Items, so a duplicate does not fail where it is created — it fails at the first
// TryGetValue BC makes, which for a temp-table read is inside
// TempTableDataProvider.TryGetRanges. DuplicateItemsAreFatalDownstream below is that claim,
// measured against the real Ncl rather than assumed.
//
// The BC-behaviour half — what a real service tier answers for a query whose runtime filter
// and DataItemTableFilter name one field, and for a self-join filtering one field twice — is
// corpus codeunit 60503 (corpus PR #370). These tests pin the runner-side mechanism only.
using System;
using System.Collections.Generic;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class QueryFilterDuplicateFieldMergeTests
{
    private readonly BcEngineFixture _engine;
    public QueryFilterDuplicateFieldMergeTests(BcEngineFixture engine) => _engine = engine;

    private const BindingFlags AnyStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static Assembly Ncl => typeof(FilterExpression).Assembly;

    private static Type NclType(string name) =>
        Ncl.GetType("Microsoft.Dynamics.Nav.Runtime." + name)
        ?? throw new InvalidOperationException(name + " is not reachable in Ncl.");

    /// <summary>
    /// Prime the private statics <c>TranslateQueryFilters</c> would have primed from a live
    /// provider, then run the runner's own <c>EnsureFilterReflection</c>. Every value written
    /// here is the one production writes, so a member test cannot leave the statics in a state
    /// production would not produce.
    /// </summary>
    private void PrimeFilterReflection()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var rp = typeof(RecordPatches);
        var buffer = rp.GetField("_tReadOnlyRecordBuffer", AnyStatic)
            ?? throw new InvalidOperationException("RecordPatches._tReadOnlyRecordBuffer is gone.");
        buffer.SetValue(null, NclType("ReadOnlyRecordBuffer"));
        // The parameterless overload, pinned by signature: RecordPatches declares a second
        // EnsureFilterReflection(object), and GetMethod(name, flags) throws on the ambiguity.
        (rp.GetMethod("EnsureFilterReflection", AnyStatic, binder: null,
                      types: Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException("RecordPatches.EnsureFilterReflection() is gone."))
            .Invoke(null, null);
    }

    /// <summary>A distinct <c>INavFieldMetadata</c> per NclType — <c>NavFieldMetadata.Equals</c>
    /// compares the value metadata, so two fields of ONE type would be one key, not two.</summary>
    private static object Field(NavNclType nclType)
    {
        var valueMeta = NclType("NavValueMetadata")
            .GetMethod("DefaultMetadata", AnyStatic, binder: null,
                       types: new[] { typeof(NavNclType) }, modifiers: null)!
            .Invoke(null, new object[] { nclType })!;
        var m = NclType("NavFieldMetadata").GetMethod("DefaultMetadata", AnyStatic)
            ?? throw new InvalidOperationException("NavFieldMetadata.DefaultMetadata is gone.");
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        args[0] = valueMeta;
        for (var i = 1; i < ps.Length; i++) args[i] = ps[i].DefaultValue;
        return m.Invoke(null, args)!;
    }

    private static object Tuple2(object field, FilterExpression expr)
    {
        var rp = typeof(RecordPatches);
        var tField = (Type)rp.GetField("_tNavFieldMetadata", AnyStatic)!.GetValue(null)!;
        var tExpr = (Type)rp.GetField("_tFilterExpr", AnyStatic)!.GetValue(null)!;
        return Activator.CreateInstance(
            typeof(Tuple<,>).MakeGenericType(tField, tExpr), field, expr)!;
    }

    private static object BuildDictionary(List<object> tuples) =>
        (typeof(RecordPatches).GetMethod("BuildFilterFieldDictionary", AnyStatic)
         ?? throw new InvalidOperationException("RecordPatches.BuildFilterFieldDictionary is gone."))
        .Invoke(null, new object[] { tuples })!;

    private static Array Items(object filterFieldDictionary) =>
        (Array)filterFieldDictionary.GetType().GetProperty("Items", AnyInstance)!
            .GetValue(filterFieldDictionary)!;

    private static (object Key, FilterExpression Value) Entry(Array items, int index)
    {
        var t = items.GetValue(index)!;
        return (t.GetType().GetProperty("Item1")!.GetValue(t)!,
                (FilterExpression)t.GetType().GetProperty("Item2")!.GetValue(t)!);
    }

    [Fact]
    public void TwoConditionsOnOneField_BecomeOneEntryAndingBoth()
    {
        PrimeFilterReflection();
        var field = Field(NavNclType.NavInteger);
        var first = new BooleanConstantFilterExpression(true);
        var second = new BooleanConstantFilterExpression(false);

        var items = Items(BuildDictionary(
            new List<object> { Tuple2(field, first), Tuple2(field, second) }));

        Assert.Equal(1, items.Length);
        var (key, value) = Entry(items, 0);
        Assert.Same(field, key);
        // Both conditions survive, in order — not "the last one wins", which would answer
        // `second` here and silently drop the dataitem's own static table filter.
        var and = Assert.IsType<BinaryFilterExpression>(value);
        Assert.Equal(FilterExpressionType.And, and.ExpressionType);
        Assert.Same(first, and.Left);
        Assert.Same(second, and.Right);
    }

    [Fact]
    public void ConditionsOnDifferentFields_StayTwoEntries()
    {
        PrimeFilterReflection();
        var integerField = Field(NavNclType.NavInteger);
        var booleanField = Field(NavNclType.NavBoolean);
        Assert.NotEqual(integerField, booleanField); // the premise: these are two keys, not one.
        var first = new BooleanConstantFilterExpression(true);
        var second = new BooleanConstantFilterExpression(false);

        var items = Items(BuildDictionary(
            new List<object> { Tuple2(integerField, first), Tuple2(booleanField, second) }));

        Assert.Equal(2, items.Length);
        var keys = new HashSet<object>(new[] { Entry(items, 0).Key, Entry(items, 1).Key });
        Assert.Contains(integerField, keys);
        Assert.Contains(booleanField, keys);
    }

    [Fact]
    public void DuplicateItemsAreFatalDownstream_WhichIsWhyTheMergeExists()
    {
        PrimeFilterReflection();
        var field = Field(NavNclType.NavInteger);
        var tField = (Type)typeof(RecordPatches).GetField("_tNavFieldMetadata", AnyStatic)!.GetValue(null)!;
        var tExpr = (Type)typeof(RecordPatches).GetField("_tFilterExpr", AnyStatic)!.GetValue(null)!;
        var tupleType = typeof(Tuple<,>).MakeGenericType(tField, tExpr);
        var pair = Array.CreateInstance(tupleType, 2);
        pair.SetValue(Tuple2(field, new BooleanConstantFilterExpression(true)), 0);
        pair.SetValue(Tuple2(field, new BooleanConstantFilterExpression(false)), 1);

        // The constructor the merge replaced: FilterFieldDictionary(IEnumerable<Tuple<...>>).
        // It accepts the duplicate without complaint — the failure is deferred.
        var tFfd = (Type)typeof(RecordPatches).GetField("_tFilterFieldDictionary", AnyStatic)!.GetValue(null)!;
        var ienum = typeof(IEnumerable<>).MakeGenericType(tupleType);
        var ctor = tFfd.GetConstructor(AnyInstance, binder: null, types: new[] { ienum }, modifiers: null)
            ?? throw new InvalidOperationException("FilterFieldDictionary(IEnumerable<Tuple<..>>) is gone.");
        var dictionary = ctor.Invoke(new object[] { pair });
        Assert.Equal(2, Items(dictionary).Length);

        // …until BC materialises the lookup, which every TryGetValue does.
        var tryGetValue = tFfd.GetMethod("TryGetValue", AnyInstance)
            ?? throw new InvalidOperationException("KeyValueSortedDictionary.TryGetValue is gone.");
        var args = new object?[] { field, null };
        var raised = Assert.Throws<TargetInvocationException>(() => tryGetValue.Invoke(dictionary, args));
        var inner = Assert.IsType<ArgumentException>(raised.InnerException);
        Assert.Contains("same key", inner.Message, StringComparison.OrdinalIgnoreCase);
    }
}
