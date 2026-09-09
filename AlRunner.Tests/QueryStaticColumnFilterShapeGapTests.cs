// QueryStaticColumnFilterShapeGapTests — issue #3660.
//
// WHAT IS BEING PROVED
//   GetStaticColumnFilters reads NCLMetaQuery.ColumnFilters — the query's static AL
//   `ColumnFilter` conditions (#2418) — through reflection, because the property is
//   internal to Ncl.dll. Its guard was
//
//       _pNCLMetaQueryColumnFilters ??= _tNCLMetaQuery!.GetProperty("ColumnFilters", ...);
//       var raw = _pNCLMetaQueryColumnFilters?.GetValue(metaAppObj) as IEnumerable;
//       if (raw == null) yield break;
//
//   and BOTH ways to reach that `yield break` are failed reads: the GetProperty lookup
//   returning null (a BC rename), or the value not being enumerable. Neither is an answer
//   BC gives — the property is a ReadOnlyCollection that is EMPTY when the query declares
//   no ColumnFilter, never null (verified on Ncl 28.4: internal instance property,
//   ReadOnlyCollection). Both call sites — TranslateQueryFilters (single-dataitem) and
//   ApplyJoinRuntimeFilters (join) — are `foreach`es that add whatever is yielded, so a
//   failed read there is indistinguishable from "this query has no static filters", and
//   the observable is a query returning MORE ROWS THAN IT SHOULD with nothing said. That
//   is the silent-default shape .claude/rules/loud-failures.md forbids, and
//   BcShapeGapException.cs's own line puts it on the refusing side: the read could not be
//   PERFORMED.
//
//   LATENT, not live. ColumnFilters resolves on every BC version this repository tests.
//   These arms are about the BC version where it moves.
//
// WHY THE METHOD TAKES ITS QUERY TYPE AS A PARAMETER
//   The lookup is against _tNCLMetaQuery, a RecordPatches static only a loaded Ncl.dll
//   populates. Taking it as a parameter is the same idiom PR #3657 used one method away in
//   the same file (QueryDataItemFilterShapeGapTests) and PermissionMetadataShapeGapTests
//   used before that: real production code, fakes standing in for a BC type whose member
//   moved, no BC install required, and no poisoning of the statics for the rest of the
//   assembly.
//
// THE TWO REFUSALS MUST BE DISTINGUISHABLE
//   An arm asserting only "it throws" would pass on a broken implementation — #3657's agent
//   caught exactly that in its own work, where reverting one lookup still threw, via a
//   different branch. So each refusal arm here asserts the wording that belongs to ITS
//   failure mode and asserts the ABSENCE of the other's: `property not found` xor
//   `cannot be enumerated`.
//
// THE NEGATIVE CONTROL IS NOT DECORATION
//   An EMPTY ColumnFilters collection is BC's answer for a query declaring no ColumnFilter,
//   and it is the overwhelmingly common case. Turning it into an error would break every
//   ordinary query on every BC version, and no arm asserting only "it refuses now" could
//   catch that.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class QueryStaticColumnFilterShapeGapTests
{
    private const string Surface = "AL query execution (projection and filter push-down)";

    // ══ 1. THE TWO FAILURE MODES, EACH DISTINGUISHABLE FROM THE OTHER ════════════════════

    [Fact]
    public void ColumnFiltersLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(QueryWithoutColumnFilters), new QueryWithoutColumnFilters()));

        Assert.Contains("ColumnFilters", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(QueryWithoutColumnFilters), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);
        // The LOOKUP failed. That is a different fact from the property resolving and holding
        // something unusable (the arm below), and a `GetProperty(...)?.GetValue(...) as
        // IEnumerable` that collapses the two into one `yield break` satisfies an arm asserting
        // only that something threw. Asserting the absence of the OTHER mode's wording is what
        // makes this arm fail under exactly one sabotage.
        Assert.Contains("property not found", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be enumerated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnFiltersHoldingSomethingNotEnumerable_RaisesASeparatelyWordedShapeGap()
    {
        // The property IS there and resolved; its value is not a collection. Same "BC's layout
        // moved" case, different diagnosis — a reader must not be sent chasing a rename that
        // did not happen.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(QueryWhoseColumnFiltersIsAnInt), new QueryWhoseColumnFiltersIsAnInt()));

        Assert.Contains("ColumnFilters", ex.Member, StringComparison.Ordinal);
        Assert.Contains("cannot be enumerated", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("property not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnFiltersReadingNull_Refuses_BecauseBcNeverAnswersNullHere()
    {
        // On a real NCLMetaQuery this property is a ReadOnlyCollection that is EMPTY when the
        // query declares no ColumnFilter — never null. So null is not an answer either, and
        // the old `as IEnumerable` cast turned it into the same silent `yield break`.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Drain(typeof(QueryWhoseColumnFiltersIsNull), new QueryWhoseColumnFiltersIsNull()));

        Assert.Contains("ColumnFilters", ex.Member, StringComparison.Ordinal);
        Assert.Contains("read as null", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("property not found", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be enumerated", ex.Message, StringComparison.Ordinal);
    }

    // ══ 2. NEGATIVE CONTROL — the answer that must STAY silent ═══════════════════════════

    [Fact]
    public void AQueryDeclaringNoColumnFilterAtAll_StillYieldsNothing_AndDoesNotThrow()
    {
        // BC's answer for the common case: the property resolved and holds an EMPTY
        // ReadOnlyCollection, exactly the shape BuildFilterExpressionCollection leaves behind
        // for a query with no `ColumnFilter` property. Refusing here would break every
        // ordinary query on every BC version.
        var tuples = Drain(typeof(FakeQuery), new FakeQuery(EmptyLikeBc()));

        Assert.Empty(tuples);
    }

    [Fact]
    public void AColumnFiltersWhoseTuplesAreNotQueryColumns_YieldsNothing_WithoutRefusing()
    {
        // The per-tuple Item1/Item2 filter is a runner-side type test, not a BC-layout read:
        // a tuple whose Item1 is not an NCLMetaQueryColumn is skipped, silently, as before.
        // This arm exists so the refusals above cannot be satisfied by refusing on the tuple
        // walk instead of on the collection read.
        var tuples = Drain(typeof(FakeQuery),
            new FakeQuery(new List<object> { Tuple.Create<object, object>("not a column", "expr") }));

        Assert.Empty(tuples);
    }

    // ══ 3. WHAT AL CAN DO WITH THE REFUSAL: NOTHING ══════════════════════════════════════
    //
    // Under `asserterror`, absorbing this would INVERT the result — real BC reads ColumnFilters
    // fine, so the asserterror fails there and would have passed here.

    [Fact]
    public void TheRefusal_TearsThroughBothAlSeams_AndIsNotAnAbsorbableOutOfScopeSignal()
    {
        object Read() => Drain(typeof(QueryWithoutColumnFilters), new QueryWithoutColumnFilters());

        var viaAssertError = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => Read()));
        Assert.Contains("ColumnFilters", viaAssertError.Member, StringComparison.Ordinal);

        var viaTryFunction = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => Read()));
        Assert.Contains("ColumnFilters", viaTryFunction.Member, StringComparison.Ordinal);

        Assert.False(OutOfScopeMessage.TryParse(viaAssertError.Message, out _));
        Assert.Null(OutOfScopeMessage.FromException(viaAssertError));
    }

    // ══ 4. THE REFLECTION PIN — liveness is guarded separately ═══════════════════════════
    //
    // ReflectionDrivenHelperLivenessTests measures LIVENESS from IL for every RecordPatches
    // member any test names as a literal, and this file names GetStaticColumnFilters — so a
    // change leaving the method declared but no longer REACHED by production fails there.
    // What is pinned here is the half that guard cannot see: the member exists, is unique by
    // name, and takes exactly the parameters these arms pass.

    [Fact]
    public void TheHelperIsUniqueByName_AndTakesTheTwoParametersTheseArmsDriveItWith()
    {
        var candidates = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "GetStaticColumnFilters")
            .ToList();

        Assert.Single(candidates);
        Assert.Equal(
            new[] { typeof(object), typeof(Type) },
            candidates[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(
            typeof(IEnumerable<(NCLMetaQueryColumn Column, object Expr)>),
            candidates[0].ReturnType);
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives the real production helper to completion. Draining matters: it is an iterator,
    /// so nothing in it runs — and nothing throws — until the sequence is enumerated, exactly
    /// as the two production foreaches enumerate it.
    /// </summary>
    private static List<(NCLMetaQueryColumn Column, object Expr)> Drain(Type queryType, object metaAppObj)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "GetStaticColumnFilters", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "test setup: RecordPatches.GetStaticColumnFilters not found");
        try
        {
            var seq = (IEnumerable<(NCLMetaQueryColumn Column, object Expr)>)
                m.Invoke(null, new object?[] { metaAppObj, queryType })!;
            return seq.ToList();
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    /// <summary>The shape BC actually hands back for a query with no ColumnFilter: EMPTY, not null.</summary>
    private static ReadOnlyCollection<Tuple<object, object>> EmptyLikeBc()
        => new(new List<Tuple<object, object>>());

    // ── Fakes standing in for BC's NCLMetaQuery. Each names what it is missing. ──

    private sealed class QueryWithoutColumnFilters
    {
    }

    private sealed class QueryWhoseColumnFiltersIsAnInt
    {
        internal int ColumnFilters => 7;
    }

    private sealed class QueryWhoseColumnFiltersIsNull
    {
        internal object? ColumnFilters => null;
    }

    private sealed class FakeQuery
    {
        public FakeQuery(object? columnFilters) => ColumnFilters = columnFilters;
        internal object? ColumnFilters { get; }
    }
}
