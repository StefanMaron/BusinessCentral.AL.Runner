// NavReportSyncApplySetTableViewReflectionTests — pins the C# reflection CONTRACT the
// #1895 fix depends on, not "what BC does" (that claim is the companion corpus PR,
// StefanMaron/BusinessCentral.AL.Language.Tests, branch
// agent/impl-7/issue-1895-report-getfilter, which proves the AL-observable behavior
// against real BC).
//
// Root cause (see AlRunner/Patches/NavReportSync.cs,
// ApplyCallerTableViewBeforePreReport's doc comment): BC's own
// RunReportInternalCoreAsync calls DataItemIterator.ApplySetTableViewForAllDataItems()
// right after applying the caller's record parameter and BEFORE OnPreReport runs. The
// runner skipped that call, so Report.SetTableView(Rec) / the record-parameter static
// Report.Run/RunModal overload left DataItem.Record (what Record.GetFilter() reads)
// unsynced through OnPreReport — the filter only reached it once the data-item loop's
// own ApplyDataItemTableViewAndRequestFormFilters call ran, later.
//
// The fix reaches this method purely by reflection (NavReportSync.SyncRun resolves it
// off DataItemIterator, NavReport's own base type, once and caches the MethodInfo) —
// there is no compile-time reference, so nothing stops a future BC artifact from
// renaming or removing it out from under the cast. What's provable here without
// compiling and running an AL report (the corpus test does that) is that this exact
// reflection binding — the one NavReportSync's cache actually performs — still resolves
// against the real, loaded Ncl.dll for the BC version this binary was built against.
// If it stops resolving, ApplyCallerTableViewBeforePreReport's `if (... == null) return;`
// guard means the fix silently reverts to the pre-#1895 (broken) behavior instead of
// failing loudly — so this is exactly the kind of drift that must be caught here, not
// discovered downstream as a quietly-reintroduced #1895.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process (must share the serial bc-engine collection — see
// BcEngineCollection.cs comment header).
[Collection(BcEngineCollection.Name)]
public class NavReportSyncApplySetTableViewReflectionTests
{
    private readonly BcEngineFixture _engine;

    public NavReportSyncApplySetTableViewReflectionTests(BcEngineFixture engine) => _engine = engine;

    private static Type NavReportType => typeof(ITreeObject).Assembly
        .GetType("Microsoft.Dynamics.Nav.Runtime.NavReport")!;

    [SkippableFact]
    public void DataItemIterator_ApplySetTableViewForAllDataItems_StillResolvesByTheExactBindingNavReportSyncUses()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Mirror NavReportSync.SyncRun's own walk exactly: NavReport's base type IS the
        // DataItemIterator the real ApplySetTableViewForAllDataItems() lives on — not a
        // type found by name search, because that is what the shipped code does.
        var dataItemIteratorBase = NavReportType.BaseType;
        Assert.NotNull(dataItemIteratorBase);

        var applySetTableViewForAllDataItems = dataItemIteratorBase!.GetMethod(
            "ApplySetTableViewForAllDataItems",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        Assert.NotNull(applySetTableViewForAllDataItems);
        Assert.Equal(typeof(void), applySetTableViewForAllDataItems!.ReturnType);
        Assert.Empty(applySetTableViewForAllDataItems.GetParameters());
    }

    [SkippableFact]
    public void OnPreReport_StillResolvesOnNavReportItself_SoTheOrderingFixCanReachBoth()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The fix's whole claim is an ORDERING one: ApplySetTableViewForAllDataItems must
        // resolve off the base type reached from NavReport BEFORE OnPreReport (declared on
        // NavReport itself) runs. Pinning that OnPreReport is still found the same way
        // NavReportSync.SyncRun finds it (walking to the "NavReport"-named type) protects
        // the other half of that ordering claim from silently going stale the same way.
        Type? navReportBase = NavReportType;
        while (navReportBase != null && navReportBase.Name != "NavReport")
            navReportBase = navReportBase.BaseType;
        Assert.NotNull(navReportBase);

        var onPreReport = navReportBase!.GetMethod("OnPreReport",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, Type.EmptyTypes, null);
        Assert.NotNull(onPreReport);

        // And it must live on a DIFFERENT type than ApplySetTableViewForAllDataItems —
        // that's what makes this an ordering bug in the first place (two separate steps on
        // two separate levels of the hierarchy) rather than something a single virtual
        // override could have fixed.
        Assert.NotEqual(onPreReport!.DeclaringType, navReportBase.BaseType);
    }
}
