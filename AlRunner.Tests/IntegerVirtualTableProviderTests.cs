// IntegerVirtualTableProviderTests — issue #3485.
//
// A RUNNER-MECHANISM test, not a claim about BC. What the Integer table ANSWERS is BC's
// claim and is pinned upstream (al-language corpus, codeunit 60368); that the runner reaches
// those answers without materialising a row is pinned in AL by
// tests/runner-extras/integer-virtual-table-window (codeunit 64591).
//
// What neither of those can see is the wiring going missing. Until #3485 the table was served
// from an in-memory store topped up by four guards, three of them installed by Cecil prepends
// — and CLAUDE.md records why that is worth a test of its own: "an orphaned hook and a live
// one look identical". The same is true of the seam that replaced them. Handing out a
// TempTableDataProvider again is not a compile error anywhere; it would answer every Integer
// read from an empty store, which is exactly the silent zero #2350 was filed for.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class IntegerVirtualTableProviderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string RecordPatchesSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RecordPatches.DataAccessDispatch.cs"));

    private static string RewriteSource => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.Runtime.cs"));

    private static Assembly Ncl => typeof(NCLMetaTable).Assembly;

    [Fact]
    public void TheHandoutForTable2000000026_CallsBcsOwnVirtualDataAccessFactory()
    {
        var m = typeof(RecordPatches).GetMethod(
            "GetIntegerVirtualDataAccess",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);

        Assert.True(m != null,
            "RecordPatches.GetIntegerVirtualDataAccess is gone. It is the whole of #3485: the "
            + "DataAccess handed out for 2000000026 must come from BC's own virtual-table "
            + "factory, not from CreateTempDataAccess.");

        var ps = m!.GetParameters();
        Assert.Equal(2, ps.Length);
        Assert.Equal(typeof(object), ps[0].ParameterType);
        Assert.Equal(typeof(NCLMetaTable), ps[1].ParameterType);
    }

    [Fact]
    public void BcsOwnFactory_HasTheShapeTheHandoutBindsTo()
    {
        // The bind is by name and signature at runtime, so a BC rename is not a compile error
        // here — it is an InvalidOperationException on the first Record Integer read of a run.
        // This says so at build time instead, and names what changed.
        var dataAccessSource = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessSource");
        Assert.True(dataAccessSource != null,
            "Microsoft.Dynamics.Nav.Runtime.DataAccessSource is gone from Ncl.");

        var factory = dataAccessSource!.GetMethod(
            "GetVirtualDataAccess",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(NCLMetaTable) }, modifiers: null);

        Assert.True(factory != null,
            "DataAccessSource.GetVirtualDataAccess(NCLMetaTable) is gone. That is the method "
            + "GetIntegerVirtualDataAccess reflection-invokes; without it every Record Integer "
            + "read fails loudly rather than answering from a store.");
    }

    [Fact]
    public void BcsIntegerProvider_IsStillTheComputedOneWeRelyOn()
    {
        // The reason this change is a faithfulness fix rather than a wider window: BC's own
        // provider computes rows and stores none. If IntegerDataProvider ever stopped being a
        // RangeBasedComputedDataProvider, "no rows are materialised" would stop being true and
        // the row cap that #3485 deleted would be missed rather than obsolete.
        var provider = Ncl.GetType("Microsoft.Dynamics.Nav.Runtime.IntegerDataProvider");
        Assert.True(provider != null, "Microsoft.Dynamics.Nav.Runtime.IntegerDataProvider is gone from Ncl.");

        var bases = new List<string>();
        for (var t = provider!.BaseType; t != null; t = t.BaseType) bases.Add(t.Name);

        Assert.Contains("RangeBasedComputedDataProvider", bases);
        Assert.Contains("ComputedVirtualDataProvider", bases);
        Assert.Contains("VirtualDataProvider", bases);
    }

    [Fact]
    public void NothingMaterialisesTheIntegerTableAnyMore()
    {
        // The half a reader cannot see from the seam alone. A re-added populate would not
        // conflict with the handout — it would quietly reintroduce a store whose contents an
        // open-ended filter is then answered from, which is the divergence #3485 removed.
        var src = RecordPatchesSource;

        var at = src.IndexOf("IsIntegerVirtualTable(table)", StringComparison.Ordinal);
        Assert.True(at >= 0, "The Integer branch is gone from GetDataAccessForTableCore.");

        var branch = src.Substring(at, Math.Min(600, src.Length - at));
        Assert.True(branch.Contains("GetIntegerVirtualDataAccess", StringComparison.Ordinal),
            "The Integer branch of GetDataAccessForTableCore no longer hands out BC's virtual "
            + "DataAccess.");
        Assert.False(branch.Contains("_mCreateTempDataAccess", StringComparison.Ordinal),
            "The Integer branch creates a temp DataAccess again. That store is empty, and an "
            + "empty Integer table makes every `dataitem(N; Integer)` report body simply not run "
            + "(#2350).");
        Assert.DoesNotContain("PopulateIntegerVirtualTable", src, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DataAccess_IntegerWindowGuardForCount")]
    [InlineData("DataAccess_IntegerWindowGuardForExists")]
    [InlineData("DataAccess_IntegerWindowGuardForGet")]
    public void NoWindowGuardIsWiredForTheIntegerTableAnyMore(string helperName)
    {
        // Each of these prepends ran on EVERY table's Count/Exists/Get — one comparison and a
        // return for all but 2000000026. Leaving a dead one wired costs that comparison on every
        // read in the run and, worse, reads as a live guard to the next person.
        Assert.False(RewriteSource.Contains(helperName, StringComparison.Ordinal),
            $"{helperName} is still registered. Since #3485 there is no materialised window for "
            + "it to widen: BC's IntegerDataProvider computes rows per request.");
        Assert.True(typeof(RecordPatches).GetMethod(helperName,
            BindingFlags.Public | BindingFlags.Static) == null,
            $"RecordPatches.{helperName} still exists.");
    }
}
