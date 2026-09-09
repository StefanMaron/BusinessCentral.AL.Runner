// QueryJoinFindRequestShapeGapTests — issue #3656.
//
// WHAT IS BEING PROVED
//   BuildTableFindAllRequest builds the per-dataitem read request for a MULTI-DATAITEM (join)
//   query, and reaches that dataitem's own static DataItemTableFilter through the same BC
//   member #3647 is about. Its failed reflection lookups used to answer a DEFAULT:
//
//       object? filtersAndMarks = null;
//       try { var p = dataItem.GetType().GetProperty("TableFiltersAndMarks", ...);
//             filtersAndMarks = p?.GetValue(dataItem); }
//       catch { filtersAndMarks = null; }
//       filtersAndMarks ??= StaticMember("FiltersAndMarks", "Empty");
//
//   At the one call site — JoinExecutor.ReadDataItemRows — FiltersAndMarks.Empty is exactly
//   what a dataitem that genuinely declares no filter yields, so an unresolved member is
//   INDISTINGUISHABLE from a real empty answer. The join then reads the dataitem's table
//   UNFILTERED and returns MORE ROWS than it should, with nothing thrown.
//
//   The `return null` when the FindProviderRequest constructor is not found is worse still:
//   ReadDataItemRows reads `if (req == null) return result;` and yields ZERO rows for that
//   dataitem, which collapses the entire join to empty. Silently.
//
// THE BARE `catch { }` WAS THE SECOND HALF OF THE DEFECT
//   `catch { filtersAndMarks = null; }` swallows EVERYTHING the read raises — including a
//   BcShapeGapException raised beneath it, the one exception type that exists precisely to
//   tear through both of AL's trapping seams. Converting the lookups to throw without dealing
//   with the catch would have produced refusals that this very method eats: a guard that is
//   quiet rather than armed. Arm 4 below is what proves the refusals now escape.
//
// LATENT, NOT LIVE. NCLMetaQueryDataItem.TableFiltersAndMarks is a real (internal) property on
//   every BC version this repository tests — confirmed on the bc284 context, where it reads
//   `internal FiltersAndMarks TableFiltersAndMarks`. These arms are about the BC version where
//   it moves.
//
// WHY A NULL TableFiltersAndMarks MUST STAY SILENT — measured, not assumed
//   BC's own NCLMetaQuery.CreateTableFiltersAndMarksFromDataItemFieldFilters (bc284) opens with
//   `if (fieldFilters.Count == 0) return null;` and ends with `return null;` when no filter
//   expression was built. So null IS BC's answer for "this dataitem declares no
//   DataItemTableFilter", and the `?? FiltersAndMarks.Empty` fallback for a SUCCESSFUL read of
//   null is correct. Converting it would break every join whose dataitems carry no filter,
//   which is most of them. The negative controls below are what stop that.
//
// WHY THE METHOD TAKES ITS BC TYPES AS PARAMETERS
//   Same idiom as PermissionMetadataShapeGapTests and #3647's QueryDataItemFilterShapeGapTests:
//   real production code driven with fakes standing in for a BC type whose member moved, no BC
//   install required. The alternative — poking the RecordPatches statics — would leave them
//   poisoned for every other test in the assembly.
using System;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class QueryJoinFindRequestShapeGapTests
{
    private const string Surface = "AL query execution (multi-dataitem join)";

    // ══ 1. THE LOOKUPS THAT MUST REFUSE ══════════════════════════════════════════════════
    //
    // Each arm removes exactly ONE member and leaves the others in place, so a fix that threw
    // unconditionally would still have to name the right member to pass — and the negative
    // controls in section 2 would fail it outright.

    [Fact]
    public void TableFiltersAndMarksLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        // The member #3656 names: the one whose disappearance drops the join's filter silently.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Build(dataItem: new DataItemWithoutTableFilters()));

        Assert.Contains("TableFiltersAndMarks", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(DataItemWithoutTableFilters), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);

        // The LOOKUP failed. That is a different fact from the property being present and
        // reading null, which is BC's own "no filters" answer and must NOT throw (section 2).
        // Without this pair a `GetProperty(...)?.GetValue(...)` that collapses the two would
        // still pass this arm, which is exactly the unarmed-guard defect #3647 caught in its
        // own DataItems arm.
        Assert.Contains("property not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersAndMarksEmptyLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        // The FALLBACK itself is a reflection lookup. If `FiltersAndMarks.Empty` moves, the old
        // code fed a null filtersAndMarks into the ctor — a different silent wrong answer for
        // the same reason.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Build(famType: typeof(FiltersAndMarksWithoutEmpty)));

        Assert.Contains("Empty", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(FiltersAndMarksWithoutEmpty), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TableFilterDictionaryEmptyLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Build(tfdType: typeof(TableFilterDictionaryWithoutEmpty)));

        Assert.Contains("Empty", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(TableFilterDictionaryWithoutEmpty), ex.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFindProviderRequestConstructorLookup_Refuses_RatherThanReturningNull()
    {
        // `return null` here is the WORST of the silent exits: ReadDataItemRows reads it as
        // "this dataitem has no rows" and collapses the whole join to empty.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Build(findReqType: typeof(FindRequestWithNoUsableCtor)));

        Assert.Contains(nameof(FindRequestWithNoUsableCtor), ex.Member, StringComparison.Ordinal);
        Assert.Contains("constructor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Surface, ex.Surface);
    }

    // ══ 2. NEGATIVE CONTROLS — the exits that must STAY silent ═══════════════════════════
    //
    // Without these, "a failed lookup refuses" is satisfiable by a change that refuses on
    // everything, which would break every join whose dataitems carry no DataItemTableFilter —
    // that is, most joins on every BC version.

    [Fact]
    public void ADataItemWhoseTableFiltersAndMarksReadsNull_GetsEmpty_AndDoesNotThrow()
    {
        // BC's own answer for "no DataItemTableFilter on this dataitem": the property IS there
        // and returns null (CreateTableFiltersAndMarksFromDataItemFieldFilters, bc284).
        var req = Build(dataItem: new FakeDataItem(null));

        var built = Assert.IsType<FakeFindProviderRequest>(req);
        Assert.Same(FakeFiltersAndMarks.Empty, built.FiltersAndMarks);
    }

    [Fact]
    public void ADataItemThatDeclaresARealFilter_GetsThatFilter_NotTheEmptyFallback()
    {
        // The positive half of the pair above: proves the fallback is a FALLBACK and not what
        // every dataitem gets. Without this arm a fix that always answered Empty would pass.
        var real = new object();

        var req = Build(dataItem: new FakeDataItem(real));

        var built = Assert.IsType<FakeFindProviderRequest>(req);
        Assert.Same(real, built.FiltersAndMarks);
        Assert.NotSame(FakeFiltersAndMarks.Empty, built.FiltersAndMarks);
    }

    [Fact]
    public void TheAbsentMemberAndTheNullRead_AreDistinguishedByWhetherAnythingIsThrownAtAll()
    {
        // The pairing that makes the two arms above ARMED rather than merely quiet, stated in
        // one place so neither half can be weakened alone. #3647's agent found its equivalent
        // arm unarmed because both of ITS outcomes refused and only the wording separated them;
        // here they are separated by the strongest discriminator available — one throws and the
        // other returns a request — so a fix that collapsed them cannot pass both lines.
        var absent = Record.Exception(() => Build(dataItem: new DataItemWithoutTableFilters()));
        var readNull = Record.Exception(() => Build(dataItem: new FakeDataItem(null)));

        var gap = Assert.IsType<BcShapeGapException>(absent);
        Assert.Contains("property not found", gap.Message, StringComparison.Ordinal);
        Assert.Null(readNull);
    }

    // ══ 3. THE REST OF THE REQUEST IS STILL BUILT ════════════════════════════════════════

    [Fact]
    public void TheRequestCarriesTheTableAndTheEmptyGlobalFilters()
    {
        var table = new NCLMetaApplicationObject();

        var built = Assert.IsType<FakeFindProviderRequest>(
            Build(dataItem: new FakeDataItem(null), table: table));

        Assert.Same(table, built.MetaApplicationObject);
        Assert.Same(FakeTableFilterDictionary.Empty, built.GlobalAndSecurityFilters);
    }

    // ══ 4. THE BARE `catch` — the half that makes the refusals reachable at all ═══════════

    [Fact]
    public void AShapeGapRaisedInsideTheRead_ReachesTheCaller_RatherThanBeingSwallowed()
    {
        // The issue's condition 3. A getter that itself raises a BcShapeGapException stands in
        // for a nested reflection read refusing beneath this one. The old `catch { }` ate it
        // and answered FiltersAndMarks.Empty — a refusal converted back into a silent default,
        // one frame below where it was raised.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Build(dataItem: new DataItemWhoseGetterRaisesAShapeGap()));

        Assert.Equal("Nested.Member", ex.Member);
        Assert.Contains("raised beneath the TableFiltersAndMarks read", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryExceptionFromTheRead_AlsoReachesTheCaller_RatherThanReadingAsNoFilters()
    {
        // The catch was not merely too wide for BcShapeGapException — ANY throw from the getter
        // became "no filters". BC's own getter can throw NavNotSupportedException (a
        // DataItemTableFilter on a FlowField); swallowing that reported the wrong answer for the
        // query instead of the error BC raises.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(dataItem: new DataItemWhoseGetterThrows()));

        Assert.Equal("the getter refused", ex.Message);
    }

    // ══ 4b. THE ADAPTER'S OWN TYPE LOOKUPS ═══════════════════════════════════════════════
    //
    // Join_BuildFindAllRequest resolves the four BC types the builder reads members off. Those
    // were `nclAsm.GetType(...)!` — the same null-forgiving shape, one frame up: a MOVED TYPE
    // would hand back null and the NullReferenceException would land inside the builder, on a
    // line naming a parameter instead of the type that went missing. Driving the adapter with a
    // provider from an assembly that declares none of them exercises the first of the four.

    [Fact]
    public void TheAdapterRefuses_NamingTheBcTypeItCouldNotResolve_RatherThanHandingNullOn()
    {
        var m = typeof(RecordPatches).GetMethod(
                    "Join_BuildFindAllRequest", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "test setup: RecordPatches.Join_BuildFindAllRequest not found");

        // `this` test assembly declares no Microsoft.Dynamics.Nav.Runtime.* type, so every
        // NclType(...) lookup fails against it — the first one is the one that reports.
        var ex = Assert.Throws<BcShapeGapException>(() =>
        {
            try
            {
                m.Invoke(null, new object?[]
                {
                    new object(), new FakeDataItem(null), new NCLMetaApplicationObject(),
                });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        });

        Assert.Contains("Microsoft.Dynamics.Nav.Runtime.", ex.Member, StringComparison.Ordinal);
        Assert.Contains("type not found", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);
    }

    // ══ 5. THE REFLECTION PIN: name, arity, uniqueness — liveness is guarded separately ═══
    //
    // ReflectionDrivenHelperLivenessTests measures LIVENESS from IL for every RecordPatches
    // member any test names as a literal, and this file names BuildTableFindAllRequest — so a
    // change that leaves the method declared but stops production reaching it fails there.
    // What is pinned HERE is the half that guard cannot see.

    [Fact]
    public void TheHelperIsUniqueByName_AndTakesTheParametersTheseArmsDriveItWith()
    {
        var candidates = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "BuildTableFindAllRequest")
            .ToList();

        Assert.Single(candidates);
        Assert.Equal(
            new[] { typeof(object), typeof(object), typeof(object),
                    typeof(Type), typeof(Type), typeof(Type), typeof(Type) },
            candidates[0].GetParameters().Select(p => p.ParameterType).ToArray());
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    private static object? Build(
        object? dataItem = null, object? table = null,
        Type? findReqType = null, Type? famType = null,
        Type? tfdType = null, Type? findTypeEnum = null)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "BuildTableFindAllRequest", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "test setup: RecordPatches.BuildTableFindAllRequest not found");
        try
        {
            return m.Invoke(null, new object?[]
            {
                new object(),                                   // provider — unused by the fakes
                dataItem ?? new FakeDataItem(null),
                table ?? new NCLMetaApplicationObject(),
                findReqType ?? typeof(FakeFindProviderRequest),
                famType ?? typeof(FakeFiltersAndMarks),
                tfdType ?? typeof(FakeTableFilterDictionary),
                findTypeEnum ?? typeof(FakeFindType),
            });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    // ── Fakes standing in for BC's types. Each names the member it is missing. ──

    private enum FakeFindType { Normal = 3, Other = 9 }

    // production reads `lockState` with Enum.ToObject(ps[i].ParameterType, 0), so the fake's
    // parameter must be an enum exactly as BC's LockState is.
    private enum FakeLockState { None = 0, Some = 1 }

    private sealed class FakeFiltersAndMarks
    {
        public static readonly FakeFiltersAndMarks Empty = new();
    }

    private sealed class FiltersAndMarksWithoutEmpty
    {
    }

    private sealed class FakeTableFilterDictionary
    {
        public static readonly FakeTableFilterDictionary Empty = new();
    }

    private sealed class TableFilterDictionaryWithoutEmpty
    {
    }

    private sealed class FakeDataItem
    {
        public FakeDataItem(object? tableFiltersAndMarks) => TableFiltersAndMarks = tableFiltersAndMarks;
        public object? TableFiltersAndMarks { get; }
    }

    private sealed class DataItemWithoutTableFilters
    {
    }

    private sealed class DataItemWhoseGetterThrows
    {
        public object? TableFiltersAndMarks => throw new InvalidOperationException("the getter refused");
    }

    private sealed class DataItemWhoseGetterRaisesAShapeGap
    {
        public object? TableFiltersAndMarks => throw new BcShapeGapException(
            Surface, "Nested.Member",
            "raised beneath the TableFiltersAndMarks read, standing in for a nested reflection "
            + "read that refused");
    }

    /// <summary>
    /// Stands in for BC's FindProviderRequest: 14 parameters, the second one named so the
    /// production ctor filter (>= 13 parameters, parameter 1 typed NCLMetaApplicationObject)
    /// selects it. Parameter NAMES are what production switches on, so they are load-bearing.
    /// </summary>
    private sealed class FakeFindProviderRequest
    {
        public FakeFindProviderRequest(
            int companyToken, NCLMetaApplicationObject metaApplicationObject, FakeLockState lockState,
            object? filtersAndMarks, object? globalAndSecurityFilters, int flowFieldSecurityFiltering,
            object? autoCalcFields, object? sortingFields, FakeFindType findType,
            int topNumberOfRowsToReturn, int skipNumberOfRows, int fastNumberOfRowsToReturn,
            object? timeout, object? fieldLoadInfo)
        {
            MetaApplicationObject = metaApplicationObject;
            FiltersAndMarks = filtersAndMarks;
            GlobalAndSecurityFilters = globalAndSecurityFilters;
            FindType = findType;
        }

        public object? MetaApplicationObject { get; }
        public object? FiltersAndMarks { get; }
        public object? GlobalAndSecurityFilters { get; }
        public FakeFindType FindType { get; }
    }

    private sealed class FindRequestWithNoUsableCtor
    {
        public FindRequestWithNoUsableCtor(int tooFew) { _ = tooFew; }
    }
}

/// <summary>
/// Production selects the FindProviderRequest ctor by testing
/// <c>GetParameters()[1].ParameterType.Name == "NCLMetaApplicationObject"</c>, so the fake's
/// second parameter must be a type of exactly that NAME. Declared at namespace scope because a
/// nested type's <c>Name</c> would still be the simple name but the shape reads more clearly
/// here; the runner never resolves it by namespace.
/// </summary>
internal sealed class NCLMetaApplicationObject
{
}
