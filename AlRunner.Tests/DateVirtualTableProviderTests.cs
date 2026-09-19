// DateVirtualTableProviderTests — issue #3506, the Date twin of IntegerVirtualTableProviderTests.
//
// A RUNNER-MECHANISM test, not a claim about BC. What the Date table ANSWERS is BC's claim and
// is pinned upstream (al-language corpus, codeunit 60983); that the runner reaches those
// answers without materialising a row is pinned in AL by
// tests/runner-extras/date-virtual-table-window (codeunit 64561).
//
// What neither of those can see is the wiring going missing. Until #3506 the table was served
// from an in-memory store topped up by four request-carrying guards — three of them installed
// by Cecil prepends — plus a FlowField-side net, and CLAUDE.md records why that is worth a
// test of its own: "an orphaned hook and a live one look identical". The same is true of the
// seam that replaced them. Handing out a TempTableDataProvider again is not a compile error
// anywhere; it would answer every Date read from an empty store, which is the
// "There is no Date within the filter." failure #2309 was filed for.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class DateVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string RecordPatchesSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs"));

    private static string FlowFieldSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "FlowFieldPatches.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    [Fact]
    public void TheHandoutForTable2000000007_CallsBcsOwnVirtualDataAccessFactory()
    {
        var m = typeof(RecordPatches).GetMethod(
            "GetDateVirtualDataAccess",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);

        Assert.True(m != null,
            "RecordPatches.GetDateVirtualDataAccess is gone. It is the whole of #3506: the "
            + "DataAccess handed out for 2000000007 must come from BC's own virtual-table "
            + "factory, not from CreateTempDataAccess.");

        var ps = m!.GetParameters();
        Assert.Equal(2, ps.Length);
        Assert.Equal(typeof(object), ps[0].ParameterType);
        Assert.Equal(typeof(NCLMetaTable), ps[1].ParameterType);
    }

    [Fact]
    public void BcsDateProvider_IsStillTheComputedOneWeRelyOn()
    {
        // The reason this change is a faithfulness fix rather than a wider window: BC's own
        // provider computes rows and stores none. If DateDataProvider ever stopped being a
        // RangeBasedComputedDataProvider, "no rows are materialised" would stop being true and
        // the row cap #3506 deleted would be missed rather than obsolete.
        var provider = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.DateDataProvider");
        Assert.True(provider != null, "Microsoft.Dynamics.Nav.Runtime.DateDataProvider is gone from Ncl.");

        var bases = new List<string>();
        for (var t = provider!.BaseType; t != null; t = t.BaseType) bases.Add(t.Name);

        Assert.Contains("RangeBasedComputedDataProvider", bases);
        Assert.Contains("ComputedVirtualDataProvider", bases);
        Assert.Contains("VirtualDataProvider", bases);
    }

    [Fact]
    public void NothingMaterialisesTheDateTableAnyMore()
    {
        // The half a reader cannot see from the seam alone. A re-added populate would not
        // conflict with the handout — it would quietly reintroduce a store whose contents an
        // open-ended filter is then answered from, which is the divergence #3506 removed.
        var src = RecordPatchesSource;

        var at = src.IndexOf("IsDateVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The Date branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(600, src.Length - at));
        Assert.True(branch.Contains("GetDateVirtualDataAccess", StringComparison.Ordinal),
            "The Date branch of GetDataAccessForTableCore no longer hands out BC's virtual DataAccess.");
        Assert.False(branch.Contains("_mCreateTempDataAccess", StringComparison.Ordinal),
            "The Date branch creates a temp DataAccess again. That store is empty, and an empty "
            + "Date table makes every Record Date read fail with \"There is no Date within the "
            + "filter.\" (#2309).");
        Assert.DoesNotContain("PopulateDefaultDateWindow", src, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureDateStoreCoversProviderRequest", src, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DataAccess_DateWindowGuardForCount")]
    [InlineData("DataAccess_DateWindowGuardForExists")]
    [InlineData("DataAccess_DateWindowGuardForGet")]
    [InlineData("EnsureDateStoreCoversProviderRequest")]
    public void NoWindowGuardIsWiredForTheDateTableAnyMore(string helperName)
    {
        // Each of these prepends ran on EVERY table's Count/Exists/Get, or on every provider
        // read — one comparison and a return for all but 2000000007. Leaving a dead one wired
        // costs that comparison on every read in the run and, worse, reads as a live guard to
        // the next person.
        Assert.False(RewriteSource.Contains(helperName, StringComparison.Ordinal),
            $"{helperName} is still registered. Since #3506 there is no materialised window for "
            + "it to widen: BC's DateDataProvider computes rows per request.");
        Assert.True(typeof(RecordPatches).GetMethod(helperName,
            BindingFlags.Public | BindingFlags.Static) == null,
            $"RecordPatches.{helperName} still exists.");
    }

    [Fact]
    public void BcsPeriodWalk_AbsorbsTheBoundaryOverflow_SoNoRunnerSideRangeCheckIsNeeded()
    {
        // #3513. The runner's own DateMissingSpans computed the not-yet-materialised parts of a
        // requested window with `start.AddDays(-1)` / `end.AddDays(1)` and threw
        // ArgumentOutOfRangeException at the representable boundary, failing a keyed Get on
        // 2000000007. That method went with the store (#3506), so the question this pins is not
        // "is the arithmetic guarded" but "why does BC's replacement not need a guard".
        //
        // BC's answer is to let it throw and stop: DateDataProvider.EnumeratePeriods calls
        // ToNextPeriodStart — unguarded AddTicks/AddMonths/AddYears, one arm per period type —
        // inside `try { ... } catch (ArgumentOutOfRangeException) { break; }`, and
        // CountPeriodsWithinRange wraps its own bound arithmetic the same way, returning zero
        // periods. So the overflow IS the terminator, and a runner-side clamp would be both
        // redundant and a divergence: it would invent a row BC does not carry.
        //
        // That makes the catch load-bearing rather than defensive. If a BC version dropped it,
        // the boundary walk would throw again — the #3513 failure, reached through Microsoft's
        // code instead of ours — so this asserts the shape is still there.
        var provider = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.DateDataProvider");
        Assert.True(provider != null, "Microsoft.Dynamics.Nav.Runtime.DateDataProvider is gone from Ncl.");

        var walker = provider!.GetMethod("ToNextPeriodStart",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(walker != null,
            "DateDataProvider.ToNextPeriodStart is gone. It is the per-period-type step whose "
            + "overflow at the representable boundary ends the walk; without it the shape this "
            + "test reasons about no longer exists.");

        // The walk itself must still be the compiler-generated iterator BC ships, because that
        // is where the catch lives. A rewritten EnumeratePeriods returning an eager list would
        // compile, answer the ordinary cases identically, and reintroduce #3513 at the edge.
        var enumerate = provider.GetMethod("EnumeratePeriods",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        Assert.True(enumerate != null, "DateDataProvider.EnumeratePeriods is gone.");

        var stateMachine = provider.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .Any(t => t.Name.Contains("EnumeratePeriods", StringComparison.Ordinal));
        Assert.True(stateMachine,
            "DateDataProvider.EnumeratePeriods is no longer a compiler-generated iterator. The "
            + "catch(ArgumentOutOfRangeException) that ends the walk at the representable "
            + "boundary lives in that state machine (#3513).");

        // And the counting half, which decides how many periods the walk will yield before it
        // starts. Its own catch is what turns an out-of-range bound into zero rows rather than
        // an exception reaching AL.
        var counter = provider.GetMethod("CountPeriodsWithinRange",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static
            | BindingFlags.Instance);
        Assert.True(counter != null,
            "DateDataProvider.CountPeriodsWithinRange is gone. It bounds the walk and absorbs an "
            + "out-of-range filter bound into a zero-period answer (#3513).");
    }

    [Fact]
    public void TheFlowFieldPathNoLongerMaterialisesTheDateWindowBehindTheFormula()
    {
        // #3507. FlowFieldPatches called EnsureDateStoreFullyMaterialised before the formula's
        // filters were resolved, so the whole default window was built and BC's filter engine
        // then selected from it — which for a range closed outside that window selected nothing,
        // a silent 0 the four request guards never saw. With the store gone there is nothing to
        // materialise, and the source rows come from BC's own provider.
        Assert.DoesNotContain("EnsureDateStoreFullyMaterialised", FlowFieldSource, StringComparison.Ordinal);
        Assert.True(typeof(RecordPatches).GetMethod("EnsureDateStoreFullyMaterialised",
            BindingFlags.Public | BindingFlags.Static) == null,
            "RecordPatches.EnsureDateStoreFullyMaterialised still exists.");
    }
}
