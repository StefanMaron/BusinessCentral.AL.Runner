// EventSubscriptionVirtualTableWiringTests — the runner-side mechanism behind #4198.
//
// WHAT THESE PIN, AND WHAT THEY DELIBERATELY DO NOT
//   What rows BC returns for Event Subscription (2000000140) is plain BC behaviour, and it is
//   asserted upstream where a real service tier adjudicates it —
//   StefanMaron/BusinessCentral.AL.Language.Tests#374, codeunit 60955. Repeating that claim here
//   would be the runner agreeing with itself.
//
//   What is provable here, without a loaded BC runtime, is the WIRING the fix consists of: that
//   2000000140 reaches BC's own virtual-data-provider factory rather than the temp store, and
//   that the seeding path feeds BC's registry from EVERY subscriber registry the runner keeps
//   rather than only the one that happens to carry full handles.
//
//   Both are source-level assertions on purpose. The behaviour they guard is reachable only
//   with Ncl loaded and a session open, and the failure mode being guarded against is a silent
//   one: delete the dispatch branch and every read of 2000000140 quietly answers an empty store
//   again, which is exactly the pre-#4198 behaviour and fails no runtime assertion that exists
//   down here.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class EventSubscriptionVirtualTableWiringTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "AlRunner.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), $"expected {relative} to exist");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The table id constant is the one BC's own <c>GetVirtualDataProvider</c> switches on for
    /// <c>EventSubscriptionDataProvider</c>. A typo here routes nothing and breaks nothing
    /// loudly.
    /// </summary>
    [Fact]
    public void EventSubscriptionVirtualTableId_IsTheIdBcSwitchesOn()
        => Assert.Equal(2000000140, AlRunner.Patches.RecordPatches.EventSubscriptionVirtualTableId);

    /// <summary>
    /// The dispatch chain must route 2000000140 through the shared BC-factory seam, NOT through
    /// <c>CreateTempDataAccess</c> like the tables whose rows the runner projects itself. The
    /// distinction is the whole fix: this table's rows come from BC's own provider reading a
    /// registry, so a temp store would answer an empty table exactly as it did before #4198.
    /// </summary>
    [Fact]
    public void Dispatch_RoutesEventSubscriptionToBcsOwnProvider()
    {
        var dispatch = ReadSource("AlRunner/Patches/RecordPatches.DataAccessDispatch.cs");

        Assert.Contains("IsEventSubscriptionVirtualTable(table)", dispatch);
        Assert.Contains("GetEventSubscriptionVirtualDataAccess(self, table)", dispatch);

        var provider = ReadSource("AlRunner/Patches/RecordPatches.EventSubscriptionVirtualTable.cs");
        Assert.Contains("GetBcVirtualDataAccess(", provider);
        Assert.DoesNotContain("CreateTempDataAccess", provider);
    }

    /// <summary>
    /// BC's registry must be fed from every subscriber registry this runner keeps, not only
    /// <c>_byKey</c>.
    ///
    /// <para>This is the assertion with real content. <c>_byKey</c> is the only registry holding
    /// full <c>SubscriberHandle</c>s — the other four hold bare <c>MethodInfo</c> lists, because
    /// their dispatch paths need no publisher ordinal. An implementation that enumerated only
    /// the convenient one would seed BC's registry with table-trigger subscriptions alone, and
    /// the Event Subscription table would then be populated, filterable, keyed and correct for
    /// every row it held while silently omitting every codeunit-published subscription. Nothing
    /// about that reads as broken from the outside, which is why it is pinned here.</para>
    /// </summary>
    [Fact]
    public void SubscriptionMetadataSeeding_EnumeratesEverySubscriberRegistry()
    {
        var seeding = ReadSource("AlRunner/Patches/EventSubscriberPatches.SubscriptionMetadata.cs");

        // The five registries EventSubscriberPatches maintains, as named in its own declarations.
        foreach (var registry in new[]
                 { "_byKey", "_validateSubs", "_byCodeunitKey", "_byTableEventKey", "_byObjectEventKey" })
            Assert.Contains(registry, seeding);
    }

    /// <summary>
    /// The registry is bound through <c>BcShape</c>'s REQUIRED helpers, so a BC shape change
    /// refuses rather than leaving 2000000140 answering an empty store while every other signal
    /// says the seeding ran (.claude/rules/guards-need-a-third-state.md).
    /// </summary>
    [Fact]
    public void SubscriptionMetadataSeeding_RefusesRatherThanSkippingOnAShapeChange()
    {
        var seeding = ReadSource("AlRunner/Patches/EventSubscriberPatches.SubscriptionMetadata.cs");

        Assert.Contains("BcShape.RequiredField(", seeding);
        Assert.Contains("BcShapeGapException(", seeding);

        // A null-forgiving read of the registry field would reinstate the silent-empty mode.
        Assert.DoesNotContain("as IList ?? new", seeding);
    }

    /// <summary>
    /// Seeding is wired into the per-bundle injection cycle, and the bundle-reload reset drops
    /// its record. Without the reset, a server reload of a same-identity bundle would skip every
    /// re-scanned subscriber as "already seeded" while BC's registry still held the previous
    /// assembly's objects.
    /// </summary>
    [Fact]
    public void SubscriptionMetadataSeeding_RunsPerBundleAndResetsOnReload()
    {
        var patches = ReadSource("AlRunner/Patches/EventSubscriberPatches.cs");

        var injectIndex = patches.IndexOf("InjectAllUsingStoredLookup()", StringComparison.Ordinal);
        Assert.True(injectIndex >= 0, "InjectAllUsingStoredLookup not found");
        Assert.Contains("SeedSubscriptionMetadata();", patches);

        var resetIndex = patches.IndexOf("ResetForReload()", StringComparison.Ordinal);
        Assert.True(resetIndex >= 0, "ResetForReload not found");
        var reset = patches[resetIndex..];
        Assert.Contains("_subscriptionMetadataSeeded.Clear();", reset);
        Assert.Contains("_subscriptionMetadataList?.Clear();", reset);
    }
}
