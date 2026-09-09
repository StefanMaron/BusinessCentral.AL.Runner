// FlowFieldComputedProviderGuardTests — the computed-provider guard must not be able to
// FALL THROUGH into the store path it exists to protect (#3622).
//
// The reported defect, and why it is not the one this file pins
// -------------------------------------------------------------
// #3622 reports `CalcFields` over a FlowField whose CalcFormula source is the Integer virtual
// table (2000000026) crashing with a raw ArgumentException:
//
//     Field 'primaryKeySortingFields' defined on type
//     'Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider' is not a field on the target
//     object which is of type 'Microsoft.Dynamics.Nav.Runtime.IntegerDataProvider'.
//
// That exact crash was measured on 0ef9bf72~1 and is GONE on 0ef9bf72 (#3597), which added the
// computed-provider branch in CalcFlowFieldValuesCore. It is fixed, and this file does not
// re-assert it — what the Integer table answers for such a formula is BC's claim and is pinned
// upstream against a real service tier by corpus codeunit 60779
// (record/TestDateVirtualTableFlowField.al, FlowField_CountOverInteger_*), whose fixture
// DVT Flow Row field 8 is precisely the `count(Integer where(Number = field(...)))` shape.
//
// What is NOT fixed is the guard's own reachability, which is what this file pins.
//
// The residual defect
// -------------------
// The branch that routes a computed provider to BC's own path is conditional on a reflection
// handle being non-null:
//
//     if (_tTempTableDataProvider != null && !_tTempTableDataProvider.IsInstanceOfType(srcTtdp))
//
// so if that Type lookup ever fails, the guard evaluates false, control falls THROUGH it, and
// forty lines later reaches
//
//     var sortingFields = _fTtdpPrimaryKeySortingFields?.GetValue(srcTtdp);
//
// against the very IntegerDataProvider the guard was there to divert — reproducing #3622's
// ArgumentException verbatim. The `?.` absorbs a MISSING handle and then hands the surviving
// one an instance of the wrong type, so the null-check protects nothing that matters here.
//
// This is the `bc-shape-gap` shape, not an out-of-scope one: the surface is in scope and
// implemented, and the only way to reach the fall-through is for BC's own layout to stop
// matching what the reflection was written against. `.claude/rules/loud-failures.md` and
// AlRunner/Infrastructure/BcShapeGapException.cs put that squarely on BcShapeGapException —
// a bug report about the runner against a specific BC build.
//
// Why a refusal is the whole answer here, and computing is not the better one
// --------------------------------------------------------------------------
// The brief for #3622 asked whether the sorting step could simply be made not to apply, so a
// FlowField over Integer would compute instead of refusing. Measured against Ncl 28.1
// (bc-decompiler, Microsoft.Dynamics.Nav.Runtime):
//
//   IntegerDataProvider declares SIX members — .ctor, CountValuesWithinRange,
//   GetValuesWithinRangeForKeyField, FirstRecordKey, LastRecordKey, TableId. It has no
//   `primaryKeySortingFields` field AND no `Filter` method. Its base VirtualDataProvider has
//   no `Filter` either.
//
// So the store path below the guard is not a sorting step that could be skipped — it is
// `TempTableDataProvider.Filter`, a method these providers do not have, reading rows from a
// store they do not keep. Removing the sorting assumption would leave the very next line with
// nothing to call. Computing the aggregate is already what the guard DOES, by handing the
// formula to FlowFieldsHelper.CalcSingleFieldFromVirtualTableAsync — BC's own path for this
// shape. The correct answer for the fall-through is therefore to make it unreachable and name
// the gap if BC's layout ever moves, not to reimplement a second aggregation route.
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class FlowFieldComputedProviderGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Source => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "FlowFieldPatches.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    /// <summary>
    /// The three handles the store path needs must be obtained through a THROWING accessor, so
    /// a BC layout change refuses by name instead of falling through to #3622's
    /// ArgumentException.
    /// <para>Asserted against the real Ncl assembly rather than against the source text: this
    /// calls BcShape itself with the same arguments the patch uses, so it fails if the member
    /// is gone AND if the accessor stops throwing. A source scan could only see the spelling.</para>
    /// </summary>
    [Fact]
    public void TheThreeTempTableDataProviderHandles_AreObtainedThroughAThrowingAccessor()
    {
        var ttdp = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider");
        Assert.True(ttdp != null,
            "TempTableDataProvider is absent from this BC build — the store path's own type is "
            + "gone, which is itself the gap this test is about.");

        // BcShape.RequiredField walks the hierarchy and throws BcShapeGapException naming the
        // member. If BC ever drops the field, this is the refusal the runner must produce.
        var f = BcShape.RequiredField(ttdp!, "primaryKeySortingFields",
            "AL FlowField calculation", "test probe");
        Assert.Equal("primaryKeySortingFields", f.Name);

        // The negative arm: the SAME accessor, asked for a member that is not there, must
        // refuse by name rather than answer null. This is what makes the positive arm above
        // evidence of a throwing accessor rather than of a member that happens to exist.
        var gap = Assert.Throws<BcShapeGapException>(() => BcShape.RequiredField(
            ttdp!, "primaryKeySortingFieldsThatDoNotExist",
            "AL FlowField calculation", "test probe"));
        Assert.Contains("primaryKeySortingFieldsThatDoNotExist", gap.Member);
    }

    /// <summary>
    /// The guard that diverts a computed provider must not be conditional on a reflection
    /// handle. `_tTempTableDataProvider != null && !IsInstanceOfType(...)` reads false when the
    /// lookup failed, and false means "fall through into the store path" — the one outcome the
    /// guard exists to prevent (#3622).
    /// </summary>
    [Fact]
    public void TheComputedProviderGuard_DoesNotFallThroughWhenItsOwnTypeLookupFailed()
    {
        var src = Source;

        Assert.DoesNotContain("_tTempTableDataProvider != null && !_tTempTableDataProvider.IsInstanceOfType", src);

        // And the positive half: the divert must still be there. Deleting the guard outright
        // would satisfy the assertion above while restoring the crash, so the liveness arm
        // names the call the divert is FOR.
        Assert.Contains("_mCalcSingleFieldFromVirtualTable", src);
        Assert.Contains("IsInstanceOfType", src);
    }

    /// <summary>
    /// The store path's own reads must not be null-absorbing. `_f?.GetValue(x)` on a missing
    /// handle yields null and lets the call proceed with a wrong-typed receiver; that is how
    /// #3622's ArgumentException is reached with the guard bypassed.
    /// </summary>
    [Fact]
    public void TheStorePathReads_DoNotAbsorbAMissingHandle()
    {
        var src = Source;

        Assert.DoesNotContain("_fTtdpPrimaryKeySortingFields?.GetValue(", src);
        Assert.DoesNotContain("_mTtdpFilter?.Invoke(", src);
    }

    /// <summary>
    /// The claim underneath the whole design decision, asserted against BC's own metadata
    /// rather than restated in a comment: IntegerDataProvider carries neither of the two
    /// members the store path below the guard uses. This is why the fall-through cannot be
    /// repaired by relaxing the sorting step — there is no Filter to call afterwards.
    /// </summary>
    [Fact]
    public void IntegerDataProvider_HasNeitherPrimaryKeySortingFieldsNorFilter()
    {
        var integer = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.IntegerDataProvider");
        Assert.True(integer != null, "IntegerDataProvider is absent from this BC build");

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.FlattenHierarchy;

        Assert.Null(integer!.GetField("primaryKeySortingFields", All));
        Assert.Empty(integer.GetMethods(All).Where(m => m.Name == "Filter"));

        // The control: the type the store path IS written against has both, so the two
        // assertions above are about IntegerDataProvider specifically and not about a
        // BindingFlags set that finds nothing anywhere.
        var ttdp = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider");
        Assert.NotNull(ttdp!.GetField("primaryKeySortingFields", All));
        Assert.NotEmpty(ttdp.GetMethods(All).Where(m => m.Name == "Filter"));
    }
}
