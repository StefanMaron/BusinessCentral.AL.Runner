// QueryDataItemFilterShapeGapTests — issue #3647.
//
// WHAT IS BEING PROVED
//   GetSingleDataItemTableFilterTuples reaches a single-dataitem query's static
//   DataItemTableFilter through reflection (#3571). Several of its early exits were
//   `yield break` on a FAILED REFLECTION LOOKUP, and at the one call site — the
//   TranslateQueryFilters pass that pushes those tuples into the read request — a
//   `yield break` is indistinguishable from "this dataitem legitimately has no filters".
//
//   So a BC rename of NCLMetaQueryDataItem.TableFiltersAndMarks would have SILENTLY
//   UNAPPLIED the filter: the query returns more rows than it should, nothing throws, and
//   a passing test cannot tell the difference unless it asserts the row count. That is the
//   silent-default shape .claude/rules/loud-failures.md forbids, and BcShapeGapException.cs's
//   own line puts it on the refusing side — the read could not be PERFORMED.
//
//   LATENT, not live. Every BC version this repository tests resolves all four members, and
//   the live path is covered by the AL bundle in
//   tests/runner-extras/query-dataitem-filter-precompiled-dep. These arms are about what
//   happens on the BC version where one of them moves.
//
// WHY THE METHOD TAKES ITS TYPES AS PARAMETERS
//   The reads are against BC types held in RecordPatches statics (_tNCLMetaQuery,
//   _tFiltersAndMarks, _tFilterFieldDictionary) that only a loaded Ncl.dll populates. Taking
//   them as parameters is the same idiom PermissionMetadataShapeGapTests drives SetProperty
//   and BuildIncludeList through: real production code, fakes standing in for a BC type whose
//   member moved, no BC install required. Poking the statics instead would have left them
//   poisoned for every other test in the assembly.
//
// THE NEGATIVE CONTROLS ARE NOT DECORATION
//   Four of this method's exits are genuine "nothing to do" answers and MUST stay silent:
//   the multi-dataitem guard, the sub-query skip, a null TableFiltersAndMarks and a null
//   Filters. Turning any of them into an error would be a regression that no arm asserting
//   only "it throws now" could catch. Each has an arm here.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class QueryDataItemFilterShapeGapTests
{
    private const string Surface = "AL query execution (projection and filter push-down)";

    // ══ 1. THE FOUR LOOKUPS THAT MUST REFUSE ═════════════════════════════════════════════
    //
    // Each arm removes exactly ONE member and leaves the other three in place, so a fix that
    // threw unconditionally would still have to name the right member to pass — and the
    // control arms below would fail it outright.

    [Fact]
    public void QueryDefinitionLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(QueryWithoutQueryDefinition), new QueryWithoutQueryDefinition()));

        Assert.Contains("QueryDefinition", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(QueryWithoutQueryDefinition), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);
    }

    [Fact]
    public void DataItemsLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(FakeQuery), new FakeQuery(new DefinitionWithoutDataItems())));

        Assert.Contains("DataItems", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(DefinitionWithoutDataItems), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        // The LOOKUP failed, which is a different fact from the property being there and
        // reading null (the arm below). Without this the two are indistinguishable, and a
        // `GetProperty(...)?.GetValue(...)` that silently collapses them passes both.
        Assert.Contains("property not found", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("read as null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataItemsReadingNull_RaisesASeparatelyWordedShapeGap_NotTheLookupOne()
    {
        // A property that IS there and answers null. BC cannot build a query with no
        // dataitems, so this is still a refusal — but it must not masquerade as the lookup
        // having failed, or a reader chases a rename that did not happen.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(FakeQuery), new FakeQuery(new DefinitionWhoseDataItemsIsNull())));

        Assert.Contains("DataItems", ex.Member, StringComparison.Ordinal);
        Assert.Contains("read as null", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("property not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TableFiltersAndMarksLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        // The member #3647 names: the one whose disappearance drops the filter silently.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(FakeQuery),
                        new FakeQuery(new FakeDefinition(new DataItemWithoutTableFilters()))));

        Assert.Contains("TableFiltersAndMarks", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(DataItemWithoutTableFilters), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemsLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(FakeQuery),
                        new FakeQuery(new FakeDefinition(new FakeDataItem(new FakeFiltersAndMarks(
                            new DictionaryWithoutItems())))),
                        famType: typeof(FakeFiltersAndMarks),
                        dictType: typeof(DictionaryWithoutItems)));

        Assert.Contains("Items", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(DictionaryWithoutItems), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataItemsThatIsNotEnumerable_RaisesAShapeGapNamingWhatItActuallyHeld()
    {
        // A member that is PRESENT but holds a shape the runner cannot use is the same
        // "BC's layout moved" case as an absent one — BcShapeGapException.cs's own line.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(FakeQuery), new FakeQuery(new DefinitionWhoseDataItemsIsAnInt())));

        Assert.Contains("DataItems", ex.Member, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    // ══ 2. NEGATIVE CONTROLS — the exits that must STAY silent ═══════════════════════════
    //
    // Every one of these is a genuine "nothing to do" answer. Without them, "absent refuses"
    // is satisfiable by a change that refuses on everything, which would break the ordinary
    // no-filter query on every BC version.

    [Fact]
    public void ADataItemWithNoFiltersAtAll_StillYieldsNothing_AndDoesNotThrow()
    {
        // TableFiltersAndMarks resolved and BC answered null. That IS the answer "no filters".
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(new FakeDataItem(null))));

        Assert.Empty(tuples);
    }

    [Fact]
    public void AFiltersAndMarksWhoseFiltersIsNull_StillYieldsNothing_AndDoesNotThrow()
    {
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(new FakeDataItem(new FakeFiltersAndMarks(null)))),
            famType: typeof(FakeFiltersAndMarks),
            dictType: typeof(FakeFilterFieldDictionary));

        Assert.Empty(tuples);
    }

    [Fact]
    public void AMultiDataItemQuery_StillYieldsNothing_BecauseTheJoinPathAppliesThemItself()
    {
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(
                new FakeDataItem(new FakeFiltersAndMarks(new FakeFilterFieldDictionary("a"))),
                new FakeDataItem(new FakeFiltersAndMarks(new FakeFilterFieldDictionary("b"))))),
            famType: typeof(FakeFiltersAndMarks),
            dictType: typeof(FakeFilterFieldDictionary));

        Assert.Empty(tuples);
    }

    [Fact]
    public void ASynthesizedSubQueryDataItem_IsSkipped_LeavingTheQuerySingleDataItem()
    {
        // #2300's exclusion. The synthesized dataitem must NOT count toward the single-dataitem
        // test, so this query still yields its one real dataitem's filter.
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(
                new FakeDataItem(new FakeFiltersAndMarks(new FakeFilterFieldDictionary("kept"))),
                new FakeDataItem(new FakeFiltersAndMarks(new FakeFilterFieldDictionary("dropped")))
                    { SubQueryDefinition = new object() })),
            famType: typeof(FakeFiltersAndMarks),
            dictType: typeof(FakeFilterFieldDictionary));

        Assert.Equal(new object[] { "kept" }, tuples);
    }

    [Fact]
    public void ANullQueryDefinition_StillYieldsNothing_AndDoesNotThrow()
    {
        // The property resolved and answered null — an answer, not a failed read.
        var tuples = Drain(typeof(FakeQuery), new FakeQuery(null));

        Assert.Empty(tuples);
    }

    // ══ 3. THE HAPPY PATH, so none of the above is vacuous ═══════════════════════════════

    [Fact]
    public void ASingleDataItemWithFilters_YieldsExactlyThoseTuples()
    {
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(new FakeDataItem(
                new FakeFiltersAndMarks(new FakeFilterFieldDictionary("first", "second"))))),
            famType: typeof(FakeFiltersAndMarks),
            dictType: typeof(FakeFilterFieldDictionary));

        Assert.Equal(new object[] { "first", "second" }, tuples);
    }

    [Fact]
    public void ANullEntryInTheItemsArray_IsSkippedRatherThanYielded()
    {
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(new FakeDataItem(
                new FakeFiltersAndMarks(new FakeFilterFieldDictionary("kept", null, "also kept"))))),
            famType: typeof(FakeFiltersAndMarks),
            dictType: typeof(FakeFilterFieldDictionary));

        Assert.Equal(new object[] { "kept", "also kept" }, tuples);
    }

    // ══ 4. WHAT AL CAN DO WITH THE REFUSAL: NOTHING ══════════════════════════════════════
    //
    // The point of BcShapeGapException over the RunnerOutOfScopeException flavours. Under
    // `asserterror`, absorbing this would INVERT the result — real BC reads the filter fine,
    // so the asserterror fails there and would have passed here.

    [Fact]
    public void TheRefusal_TearsThroughBothAlSeams_AndIsNotAnAbsorbableOutOfScopeSignal()
    {
        object Read() => Drain(typeof(FakeQuery),
            new FakeQuery(new FakeDefinition(new DataItemWithoutTableFilters())));

        var viaAssertError = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => Read()));
        Assert.Contains("TableFiltersAndMarks", viaAssertError.Member, StringComparison.Ordinal);

        var viaTryFunction = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => Read()));
        Assert.Contains("TableFiltersAndMarks", viaTryFunction.Member, StringComparison.Ordinal);

        // Neither the reporter nor an expect-oos manifest entry may recover it, so a
        // BC-layout regression can never be declared an expected out-of-scope surface.
        Assert.False(OutOfScopeMessage.TryParse(viaAssertError.Message, out _));
        Assert.Null(OutOfScopeMessage.FromException(viaAssertError));
    }

    // ══ 5. THE REFLECTION PIN: name, arity, uniqueness — liveness is guarded separately ═══
    //
    // ReflectionDrivenHelperLivenessTests measures LIVENESS from IL for every RecordPatches
    // member any test names as a literal, and this file names GetSingleDataItemTableFilterTuples
    // — so a change that leaves the method declared but stops production reaching it fails
    // there. What is pinned HERE is the half that guard cannot see: that the member exists,
    // is unique by name, and takes exactly the four parameters these arms pass.

    [Fact]
    public void TheHelperIsUniqueByName_AndTakesTheFourParametersTheseArmsDriveItWith()
    {
        var candidates = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "GetSingleDataItemTableFilterTuples")
            .ToList();

        Assert.Single(candidates);
        Assert.Equal(
            new[] { typeof(object), typeof(Type), typeof(Type), typeof(Type) },
            candidates[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(IEnumerable<object>), candidates[0].ReturnType);
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives the real production helper to completion. Draining matters: it is an iterator,
    /// so nothing in it runs — and nothing throws — until the sequence is enumerated, exactly
    /// as the production foreach at the call site enumerates it.
    /// </summary>
    private static List<object> Drain(Type queryType, object metaAppObj,
                                      Type? famType = null, Type? dictType = null)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "GetSingleDataItemTableFilterTuples", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "test setup: RecordPatches.GetSingleDataItemTableFilterTuples not found");
        try
        {
            var seq = (IEnumerable<object>)m.Invoke(null, new object?[]
            {
                metaAppObj, queryType,
                famType ?? typeof(FakeFiltersAndMarks),
                dictType ?? typeof(FakeFilterFieldDictionary),
            })!;
            return seq.ToList();
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    // ── Fakes standing in for BC's types. Each names the member it is missing. ──

    private sealed class QueryWithoutQueryDefinition
    {
    }

    private sealed class FakeQuery
    {
        public FakeQuery(object? queryDefinition) => QueryDefinition = queryDefinition;
        public object? QueryDefinition { get; }
    }

    private sealed class DefinitionWithoutDataItems
    {
    }

    private sealed class DefinitionWhoseDataItemsIsAnInt
    {
        public int DataItems => 7;
    }

    private sealed class DefinitionWhoseDataItemsIsNull
    {
        public object? DataItems => null;
    }

    private sealed class FakeDefinition
    {
        public FakeDefinition(params object?[] items) => DataItems = items;
        public object?[] DataItems { get; }
    }

    private sealed class DataItemWithoutTableFilters
    {
        public object? SubQueryDefinition => null;
    }

    private sealed class FakeDataItem
    {
        public FakeDataItem(object? tableFiltersAndMarks) => TableFiltersAndMarks = tableFiltersAndMarks;
        public object? TableFiltersAndMarks { get; }
        public object? SubQueryDefinition { get; init; }
    }

    private sealed class FakeFiltersAndMarks
    {
        public FakeFiltersAndMarks(object? filters) => Filters = filters;
        public object? Filters { get; }
    }

    private sealed class DictionaryWithoutItems
    {
    }

    private sealed class FakeFilterFieldDictionary
    {
        public FakeFilterFieldDictionary(params object?[] items) => Items = items;
        public object?[] Items { get; }
    }
}
