// AffectedEventSelectionTests — #4988: the subscriber-change rules affectedOnly applies, including
// every path that must force a full run rather than select too few tests. The server-level proof
// is ServerAffectedSelectionEventSubscriberTests; these cover the refusals an AL fixture without
// the Base Application cannot reach.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AffectedEventSelectionTests
{
    private const string Work = "ev|Codeunit|50100|OnDoWork";

    private static SubscriberBinding Bind(string eventKey, string identity = "")
        => new("Codeunit", 50110, "Handle", eventKey, identity.Length == 0 ? eventKey : identity);

    private static EventObservability Observed(params string[] events)
        => new(events.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["Codeunit|50100"] = true, ["Table|50101"] = true });

    private static AffectedEventSelection.Result Select(
        IReadOnlyList<SubscriberBinding>? previous, IReadOnlyList<SubscriberBinding>? current,
        EventObservability? observed, bool codeChanged = false, params string[] currentPublishers)
        => AffectedEventSelection.ChangedEventKeys(previous, current, observed,
            currentPublishers.ToHashSet(StringComparer.Ordinal), _ => codeChanged);

    [Fact]
    public void AddedSubscriber_OnARecordedEvent_KeysThatEvent()
    {
        var r = Select(new List<SubscriberBinding>(), new[] { Bind(Work) }, Observed(Work));
        Assert.Null(r.ForceFullReason);
        Assert.Equal(new[] { Work }, r.Keys);
    }

    [Fact]
    public void UnchangedBindings_KeyNothing_UnlessTheSubscribersOwnCodeChanged()
    {
        Assert.Empty(Select(new[] { Bind(Work) }, new[] { Bind(Work) }, Observed(Work)).Keys);
        Assert.Equal(new[] { Work }, Select(new[] { Bind(Work) }, new[] { Bind(Work) }, Observed(Work), codeChanged: true).Keys);
    }

    [Fact]
    public void TableTriggerSubscriber_KeysTheTable_AndUnresolvedConsults()
    {
        var r = Select(new List<SubscriberBinding>(), new[] { Bind("trig|Table|50101") }, Observed());
        Assert.Null(r.ForceFullReason);
        Assert.Equal(new[] { "trig|?", "trig|Table|50101" }, r.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void NewEventOnASeededPublisher_KeysNothing()
    {
        // Not observed in the recording run, but its publisher was fully seeded then: the event is new.
        var r = Select(new List<SubscriberBinding>(), new[] { Bind("ev|Codeunit|50100|OnBrandNew") }, Observed(Work));
        Assert.Null(r.ForceFullReason);
        Assert.Empty(r.Keys);
    }

    [Fact]
    public void PublisherOutsideTheRequestModules_ForcesFull()
    {
        var r = Select(new List<SubscriberBinding>(), new[] { Bind("ev|Codeunit|80|OnBeforePostSalesDoc") }, Observed(Work));
        Assert.Contains("published outside this request's modules", r.ForceFullReason);
    }

    [Fact]
    public void PublisherNewToTheRequestModules_KeysNothing()
    {
        var r = Select(new List<SubscriberBinding>(), new[] { Bind("ev|Codeunit|50102|OnX") }, Observed(Work),
            false, "Codeunit|50102");
        Assert.Null(r.ForceFullReason);
    }

    [Fact]
    public void PublisherThatCouldNotBeSeeded_ForcesFull()
    {
        var observed = new EventObservability(new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["Codeunit|50100"] = false });
        var r = Select(new List<SubscriberBinding>(), new[] { Bind(Work) }, observed, false, "Codeunit|50100");
        Assert.Contains("could not all be recorded", r.ForceFullReason);
    }

    [Fact]
    public void PageTriggerAndUnknownObjectTypes_ForceFull()
    {
        Assert.Contains("page trigger event",
            Select(new List<SubscriberBinding>(), new[] { Bind("trig|Page|50103") }, Observed()).ForceFullReason);
        Assert.Contains("object type",
            Select(new List<SubscriberBinding>(), new[] { Bind("other|2|1|OnX") }, Observed()).ForceFullReason);
    }

    [Fact]
    public void RemovedBinding_IsTakenFromTheRecordedSide()
    {
        var r = Select(new[] { Bind(Work) }, new List<SubscriberBinding>(), Observed(Work));
        Assert.Equal(new[] { Work }, r.Keys);
    }

    [Fact]
    public void NoRecordedOrUnreadableBindings_ForceFull()
    {
        Assert.Contains("no recorded", Select(null, new[] { Bind(Work) }, Observed(Work)).ForceFullReason);
        Assert.Contains("no recorded", Select(new[] { Bind(Work) }, new[] { Bind(Work) }, null).ForceFullReason);
        Assert.Contains("could not be read", Select(new[] { Bind(Work) }, null, Observed(Work)).ForceFullReason);
    }

    [Fact]
    public void Overlaps_MissingRecordSelectsOnAnyChange_AndNeverOnNone()
    {
        var changed = new HashSet<string>(StringComparer.Ordinal) { Work };
        Assert.True(AffectedEventSelection.Overlaps(null, changed));
        Assert.True(AffectedEventSelection.Overlaps(new HashSet<string> { Work }, changed));
        Assert.False(AffectedEventSelection.Overlaps(new HashSet<string> { "ev|Codeunit|1|Other" }, changed));
        Assert.False(AffectedEventSelection.Overlaps(null, new HashSet<string>()));
    }

    [Fact]
    public void BindingEventKey_MatchesTheKeysARaiseIsRecordedUnder()
    {
        Assert.Equal("ev|Codeunit|50100|OnDoWork", EventSubscriberPatches.BindingEventKey(5, 50100, "OnDoWork"));
        Assert.Equal("trig|Table|50101", EventSubscriberPatches.BindingEventKey(1, 50101, "OnAfterInsertEvent"));
        Assert.Equal("trig|Table|50101", EventSubscriberPatches.BindingEventKey(1, 50101, "OnBeforeValidateEvent"));
        Assert.Equal("ev|Table|50101|OnCustom", EventSubscriberPatches.BindingEventKey(1, 50101, "OnCustom"));
        Assert.Equal("ev|Page|50103|OnCustom", EventSubscriberPatches.BindingEventKey(8, 50103, "OnCustom"));
        Assert.Equal("trig|Page|50103", EventSubscriberPatches.BindingEventKey(8, 50103, "OnOpenPageEvent"));
        Assert.Equal("ev|Table|50101|OnCustom", AlEventRaiseTracker.EventKey(AlEventRaiseTracker.NormalizeDispatchKind("Record"), 50101, "OnCustom"));
    }
}
