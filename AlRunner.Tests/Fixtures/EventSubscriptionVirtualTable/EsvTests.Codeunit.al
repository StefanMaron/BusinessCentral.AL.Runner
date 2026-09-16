// The Event Subscription virtual table (2000000140) on the runner — #4198.
//
// Every assertion here fails against the pre-#4198 empty store, which is the point: this
// bundle is what reds when the seeding or the dispatch branch is disabled, and a source-text
// test cannot do that (a fix wrapped in `if (false)` keeps every string a grep would find).
//
// WHAT IS ASSERTED, AND WHY EACH ARM HAS A PARTNER
//   Counting rows alone would pass against a provider answering a fixed row set or ignoring
//   its filters, so each positive arm is paired with a negative whose expected answer differs
//   and which is NOT satisfied by an empty table either:
//
//     subscriber codeunit 70764 has rows  <->  publisher codeunit 70763 has none
//     watched table 70761 has rows        <->  unwatched table 70762 has none
//
//   In both pairs the two objects are the same kind, declared in the same bundle, so only the
//   subscription registry separates them.
//
// WHAT IS NOT ASSERTED HERE
//   What real BC answers for this table is plain BC behaviour and is asserted upstream, in
//   corpus codeunit 60955 (StefanMaron/BusinessCentral.AL.Language.Tests#374), where a service
//   tier adjudicates it. Repeating those claims down here would be the runner agreeing with
//   itself.
//
//   "Event Type" for a CODEUNIT publisher is deliberately left alone: the runner reports
//   BC's own "publishing method not found" fallback there, because the publisher does not
//   resolve (#4218). The table publisher's row IS asserted, which is what keeps that column
//   from being a constant without pinning the known gap twice.
codeunit 70765 "ESV Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "ESV Assert";

    [Test]
    procedure EventSubscription_SubscriberCodeunit_HasARowPerSubscriberMethod()
    // The table is populated at all, and populated per subscriber METHOD rather than per
    // codeunit: "ESV Subscriber" declares exactly two [EventSubscriber] methods.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Subscriber Codeunit ID", Codeunit::"ESV Subscriber");
        Assert.AreEqual(
            2, EventSubscription.Count(),
            'ESV Subscriber declares two [EventSubscriber] methods, so Event Subscription must hold two rows for it.');
    end;

    [Test]
    procedure EventSubscription_PublishingOnlyCodeunit_HasNoRows()
    // The partner of the arm above. "ESV Publisher" is a codeunit in this same bundle that
    // publishes an event and subscribes to nothing, so a provider answering every row to
    // every filter fails here while passing above.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Subscriber Codeunit ID", Codeunit::"ESV Publisher");
        Assert.AreEqual(
            0, EventSubscription.Count(),
            'ESV Publisher subscribes to nothing, so Event Subscription must hold no rows for it.');
        Assert.IsTrue(
            EventSubscription.IsEmpty(),
            'IsEmpty is served by its own provider path and must agree with Count for a codeunit with no subscriptions.');
    end;

    [Test]
    procedure EventSubscription_WatchedTable_HasARowNamingItAsPublisher()
    // The publisher-side filter, positive arm.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Publisher Object Type", EventSubscription."Publisher Object Type"::Table);
        EventSubscription.SetRange("Publisher Object ID", Database::"ESV Watched");
        Assert.AreEqual(
            1, EventSubscription.Count(),
            'ESV Watched has exactly one table-event subscriber, so Event Subscription must hold one row naming it as publisher.');
    end;

    [Test]
    procedure EventSubscription_UnsubscribedTable_HasNoRows()
    // The partner of the arm above. ESV Unwatched is deliberately subscriber-free.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Publisher Object Type", EventSubscription."Publisher Object Type"::Table);
        EventSubscription.SetRange("Publisher Object ID", Database::"ESV Unwatched");
        Assert.AreEqual(
            0, EventSubscription.Count(),
            'ESV Unwatched has no subscriber in this bundle, so Event Subscription must hold no rows naming it as publisher.');
    end;

    [Test]
    procedure EventSubscription_Get_ByPrimaryKey_ReturnsThatMethodsRow()
    // (Subscriber Codeunit ID, Subscriber Function) actually keys the table: the two known
    // subscriber methods of ONE codeunit must fetch DIFFERENT rows. A provider keyed on the
    // codeunit alone, or one answering its first row, fails the second half.
    var
        EventSubscription: Record "Event Subscription";
    begin
        Assert.IsTrue(
            EventSubscription.Get(Codeunit::"ESV Subscriber", 'HandleEsvWatchedInsert'),
            'Event Subscription has no row for ESV Subscriber.HandleEsvWatchedInsert.');
        Assert.AreEqual(
            Database::"ESV Watched", EventSubscription."Publisher Object ID",
            'HandleEsvWatchedInsert subscribes to an event published by ESV Watched.');

        Assert.IsTrue(
            EventSubscription.Get(Codeunit::"ESV Subscriber", 'HandleEsvProbeEvent'),
            'Event Subscription has no row for ESV Subscriber.HandleEsvProbeEvent.');
        Assert.AreEqual(
            Codeunit::"ESV Publisher", EventSubscription."Publisher Object ID",
            'The second key value must select a DIFFERENT row of the same codeunit, not the first one again.');
    end;

    [Test]
    procedure EventSubscription_Get_UndeclaredSubscriberFunction_Fails()
    // The negative direction of the keyed read. This one passes against an empty store too --
    // it is the arm that says the table refuses rather than inventing a row, and it is stated
    // separately so the arms above carry the population claim on their own.
    var
        EventSubscription: Record "Event Subscription";
    begin
        Assert.IsFalse(
            EventSubscription.Get(Codeunit::"ESV Subscriber", 'ThisHandlerDoesNotExist'),
            'Event Subscription must not answer a row for a subscriber function that does not exist.');
    end;

    [Test]
    procedure EventSubscription_TableEventRow_CarriesTheRealMethodNames()
    // The row describes the subscription rather than merely existing: the subscriber method
    // name, the published event name, and the publisher object type all come from the
    // fixture's own source, so a row built from anything else fails.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Publisher Object Type", EventSubscription."Publisher Object Type"::Table);
        EventSubscription.SetRange("Publisher Object ID", Database::"ESV Watched");
        Assert.IsTrue(EventSubscription.FindFirst(), 'ESV Watched must have a subscription row.');

        Assert.AreEqual(
            'HandleEsvWatchedInsert', EventSubscription."Subscriber Function",
            'The row must report the AL name of the subscribing method.');
        Assert.AreEqual(
            'OnAfterInsertEvent', EventSubscription."Published Function",
            'The row must report the AL name of the published event.');
        Assert.AreEqual(
            Codeunit::"ESV Subscriber", EventSubscription."Subscriber Codeunit ID",
            'The row must report the codeunit that declares the subscriber.');
        // Compared as text: "Trigger" is an AL keyword, so the bare option member does not parse
        // and the QUOTED form resolves to the ordinal 0 instead of the member -- which compares a
        // number against a name and fails while the value is correct. Format() sidesteps both.
        // This is the resolved-publisher arm: a codeunit publisher does not resolve on the runner
        // (#4218), so asserting Event Type there too would pin that gap twice.
        Assert.AreEqual(
            'Trigger', Format(EventSubscription."Event Type"),
            'OnAfterInsertEvent is a table TRIGGER event, so the row must report Event Type Trigger.');
    end;

    [Test]
    procedure EventSubscription_UnfilteredWalk_ReachesBothPublisherKinds()
    // An unfiltered walk spans the whole registry rather than one publisher's rows, and the
    // two rows it must reach were registered through DIFFERENT runner registries -- the
    // codeunit-event one holds bare MethodInfo lists, the table-trigger one holds full
    // handles. Seeding only the convenient registry passes every arm above except this one.
    var
        EventSubscription: Record "Event Subscription";
        SawCodeunitPublisher: Boolean;
        SawTablePublisher: Boolean;
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Subscriber Codeunit ID", Codeunit::"ESV Subscriber");
        Assert.IsTrue(EventSubscription.FindSet(), 'ESV Subscriber must have subscription rows.');

        repeat
            if (EventSubscription."Publisher Object Type" = EventSubscription."Publisher Object Type"::Codeunit)
                and (EventSubscription."Publisher Object ID" = Codeunit::"ESV Publisher") then
                SawCodeunitPublisher := true;
            if (EventSubscription."Publisher Object Type" = EventSubscription."Publisher Object Type"::Table)
                and (EventSubscription."Publisher Object ID" = Database::"ESV Watched") then
                SawTablePublisher := true;
        until EventSubscription.Next() = 0;

        Assert.IsTrue(SawCodeunitPublisher, 'The walk must reach the codeunit-published subscription.');
        Assert.IsTrue(SawTablePublisher, 'The walk must reach the table-published subscription.');
    end;
}
