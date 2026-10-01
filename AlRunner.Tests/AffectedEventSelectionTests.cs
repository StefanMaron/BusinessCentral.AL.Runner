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

    // #5008 — ChangedTableKeys. The server-level proof is ServerAffectedSelectionTableChangeTests.
    private static AffectedEventSelection.Result Tables(
        (string, int?)[] changed, Dictionary<int, List<int>>? bases = null, params string[] bundleWide)
        => AffectedEventSelection.ChangedTableKeys(changed, bases ?? new Dictionary<int, List<int>>(),
            bundleWide.Length == 1 && bundleWide[0] == "<none>" ? null : bundleWide.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void ChangedTable_KeysItsRecordsAndTriggerConsults_OtherKindsKeyNothing()
    {
        var r = Tables(new (string, int?)[] { ("Table", 50101), ("Codeunit", 50100), ("Report", 50104) });
        Assert.Null(r.ForceFullReason);
        Assert.Equal(new[] { "tbl|Table|50101", "trig|?", "trig|Table|50101" }, r.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(Tables(new (string, int?)[] { ("Codeunit", 50100) }, null, "<none>").Keys);
    }

    [Fact]
    public void ChangedPageOrPageExtension_KeysNoTable()
    {
        var r = Tables(new (string, int?)[] { ("Page", 50103), ("PageExtension", 50105) });
        Assert.Null(r.ForceFullReason);
        Assert.Empty(r.Keys);
    }

    // #5025 — ChangedPageExtensionKeys. The server-level proof is ServerAffectedSelectionPageExtensionTests.
    private static AffectedEventSelection.Result PageExtensions(
        (string, int?)[] changed, Dictionary<int, List<int>>? bases, params string[] bundleWide)
        => AffectedEventSelection.ChangedPageExtensionKeys(changed, bases,
            bundleWide.Length == 1 && bundleWide[0] == "<none>" ? null : bundleWide.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void ChangedPageExtension_KeysItsBasePage_FromTheRegistryOrTheRecording()
    {
        var bases = new Dictionary<int, List<int>> { [50105] = new() { 50103 } };
        var added = PageExtensions(new (string, int?)[] { ("PageExtension", 50105), ("Codeunit", 50100), ("Page", 50104) }, bases);
        Assert.Null(added.ForceFullReason);
        Assert.Equal(new[] { "Page|id:50103", "dep|Page|id:50103" }, added.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // Removed: gone from the registry; the recording run named its base.
        var removed = PageExtensions(new (string, int?)[] { ("PageExtension", 50106) }, bases,
            AffectedEventSelection.PageExtensionBaseKey(50106, 50107), "pext|501060|1");
        Assert.Null(removed.ForceFullReason);
        Assert.Equal(new[] { "Page|id:50107", "dep|Page|id:50107" }, removed.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // Rebased: both the page it extended and the one it extends now.
        var rebased = PageExtensions(new (string, int?)[] { ("PageExtension", 50105) }, bases,
            AffectedEventSelection.PageExtensionBaseKey(50105, 50108));
        Assert.Equal(new[] { "Page|id:50103", "Page|id:50108", "dep|Page|id:50103", "dep|Page|id:50108" },
            rebased.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ChangedPageExtension_ForcesFull_WhenItsBaseCannotBeNamed_OrIsHeldOutsideATest()
    {
        var bases = new Dictionary<int, List<int>> { [50105] = new() { 50103 }, [50109] = new() };
        Assert.Contains("base page of pageextension 50109 could not be resolved",
            PageExtensions(new (string, int?)[] { ("PageExtension", 50109) }, bases).ForceFullReason);
        Assert.Contains("could not be read",
            PageExtensions(new (string, int?)[] { ("PageExtension", 50105) }, null).ForceFullReason);
        Assert.Contains("no record of which page each pageextension extends",
            PageExtensions(new (string, int?)[] { ("PageExtension", 50105) }, bases, "<none>").ForceFullReason);
        Assert.Contains("has no object id",
            PageExtensions(new (string, int?)[] { ("PageExtension", null) }, bases).ForceFullReason);
        Assert.Null(PageExtensions(new (string, int?)[] { ("Page", 50103) }, null, "<none>").ForceFullReason);

        foreach (var held in new[] { "Page|id:50103", "dep|Page|id:50103" })
            Assert.Contains("extends Page 50103, an instance of which was built outside any one test",
                PageExtensions(new (string, int?)[] { ("PageExtension", 50105) }, bases,
                    AffectedEventSelection.LongLivedObjectKey(held)).ForceFullReason);
    }

    // #5083 — UnkeyedKindChange. The server-level proof is ServerAffectedSelectionObjectKindTests.
    [Fact]
    public void UnkeyedKind_ForcesFull_NamingTheObjectAndItsKind_KeyedKindsDoNot()
    {
        static string? Reason(params AffectedObjectId[] changed) => AffectedEventSelection.UnkeyedKindChange(changed);
        static AffectedObjectId O(string kind, int? id, string name) => new(kind, id, name);

        Assert.Null(Reason());
        Assert.Null(Reason(
            O("Codeunit", 1, "C"), O("Page", 2, "P"), O("Report", 3, "R"), O("Query", 4, "Q"),
            O("XmlPort", 5, "X"), O("Table", 6, "T"), O("TableExtension", 7, "TE"), O("PageExtension", 8, "PE")));

        Assert.Equal("Enum 60741 changed, and which tests read an enum's values, captions or implementations is not recorded",
            Reason(O("Codeunit", 1, "C"), O("Enum", 60741, "E")));
        Assert.StartsWith("EnumExtension 9 changed, and which tests read an enum's",
            Reason(O("EnumExtension", 9, "EE")));
        Assert.Equal("ReportExtension 10 changed, and no test recording holds the use of this kind of object (ReportExtension)",
            Reason(O("ReportExtension", 10, "RE")));
        // A kind nothing here knows, and one declared by name only: never "changed but selects nothing".
        Assert.Equal("SomeFutureKind 11 changed, and no test recording holds the use of this kind of object (SomeFutureKind)",
            Reason(O("SomeFutureKind", 11, "F")));
        Assert.Equal("Profile Kind Profile changed, and no test recording holds the use of this kind of object (Profile)",
            Reason(O("Profile", null, "Kind Profile")));
        // An interface's extends list decides what `is`/`as` answer for implementers nobody edited.
        Assert.Equal("Interface Probe I changed, and no test recording holds the use of this kind of object (Interface)",
            Reason(O("Interface", null, "Probe I")));
    }

    // #5011 — a whole-object change to an instance no one test built.
    [Fact]
    public void LongLivedObject_ForcesFullOnWholeObjectChange_NotOnANarrowedOne()
    {
        var bundleWide = new HashSet<string>(StringComparer.Ordinal) { AffectedEventSelection.LongLivedObjectKey("Codeunit|id:50100") };
        Assert.Contains("an instance of Codeunit id:50100 was built outside any one test",
            AffectedEventSelection.LongLivedObjectChange(new[] { "Codeunit|id:50100" }, bundleWide));
        Assert.Null(AffectedEventSelection.LongLivedObjectChange(new[] { "Codeunit|id:50100::proc:P" }, bundleWide));
        Assert.Null(AffectedEventSelection.LongLivedObjectChange(new[] { "Codeunit|id:50101" }, bundleWide));
        Assert.Null(AffectedEventSelection.LongLivedObjectChange(new[] { "Codeunit|id:50100" }, null));
    }

    [Fact]
    public void ChangedTableExtension_KeysItselfAndItsCurrentBase_OrItsRecordedBase()
    {
        var bases = new Dictionary<int, List<int>> { [50120] = new() { 50101 } };
        var added = Tables(new (string, int?)[] { ("TableExtension", 50120) }, bases);
        Assert.Null(added.ForceFullReason);
        Assert.Equal(new[] { "tbl|TableExtension|50120", "tbl|Table|50101", "trig|?", "trig|Table|50101" },
            added.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // Removed: gone from the registry, but the recording run resolved its base.
        var removed = Tables(new (string, int?)[] { ("TableExtension", 50121) }, bases, "ext|50121");
        Assert.Null(removed.ForceFullReason);
        Assert.Contains("tbl|TableExtension|50121", removed.Keys);

        var unresolved = Tables(new (string, int?)[] { ("TableExtension", 50122) }, bases);
        Assert.Contains("base table of tableextension 50122 could not be resolved", unresolved.ForceFullReason);
    }

    [Fact]
    public void ChangedTable_ForcesFull_WhenARecordOfItOutlivedOneTest_OrNothingWasRecorded()
    {
        var held = Tables(new (string, int?)[] { ("Table", 50101) }, null, "tbl|Table|50101");
        Assert.Contains("a record of Table 50101 was held outside any one test", held.ForceFullReason);

        var heldViaExtension = Tables(new (string, int?)[] { ("TableExtension", 50120) },
            new Dictionary<int, List<int>> { [50120] = new() { 50101 } }, "tbl|Table|50101");
        Assert.NotNull(heldViaExtension.ForceFullReason);

        Assert.Contains("no record of which tests held records",
            Tables(new (string, int?)[] { ("Table", 50101) }, null, "<none>").ForceFullReason);
        Assert.Contains("has no object id", Tables(new (string, int?)[] { ("Table", null) }).ForceFullReason);
    }
}
