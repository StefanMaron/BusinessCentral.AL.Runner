// Event Subscription (2000000140) must be scoped to the bundle being run — issue #4222.
//
// WHAT THIS FIXTURE MEASURES, AND WHY IT NEEDS TWO BUNDLES
//   Run alone, either bundle passes every arm here even on the unfixed runner: the defect
//   needs a SECOND bundle in the same process. The runner is invoked as
//   `al-runner <AppA> <AppB>`, two one-shot bundles in one process, and the accumulation
//   shows up in whichever bundle runs second.
//
//   EventSubscriptionMultiBundleScopeTests.cs is what drives that invocation. Running this
//   directory's two bundles separately would prove nothing.
//
// THE FOREIGN ARM IS THE ONE THAT REDS
//   `ForeignSubscriptionIsAbsentB` filters on the OTHER bundle's subscriber codeunit id,
//   a number this bundle never declares. On the unfixed runner bundle B sees bundle A's row,
//   because EventSubscriberPatches' scan registries are process-wide and a one-shot
//   multi-bundle run reaches neither the watch-mode reset nor the per-request one.
//
// THE CONTROL ARM IS WHAT MAKES THE RESULT ATTRIBUTABLE
//   `AllObjDoesNotListTheForeignCodeunitB` asks a DIFFERENT virtual table the same
//   cross-bundle question. It passes on the unfixed runner too, and that is the point: it
//   says cross-bundle visibility is not something every virtual table here does, so a red
//   foreign arm is this table's scoping rather than the process model. Without it the
//   report would be a plausible guess (#4222's own filing makes the same argument).
codeunit 70804 "EMB Tests B"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "EMB Assert B";

    [Test]
    procedure OwnSubscriptionIsPresentB()
    // The positive arm. Without it the foreign arm below would pass against a table that
    // is simply empty, which is the pre-#4198 behaviour rather than a scoping fix.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Subscriber Codeunit ID", Codeunit::"EMB Subscriber B");
        Assert.AreEqual(
            1, EventSubscription.Count(),
            'This bundle declares exactly one [EventSubscriber] method, so Event Subscription must list it.');
    end;

    [Test]
    procedure ForeignSubscriptionIsAbsentB()
    // The arm this issue is about. 70783 is the OTHER bundle's subscriber codeunit,
    // an object this bundle never declares and cannot reference by name.
    var
        EventSubscription: Record "Event Subscription";
    begin
        EventSubscription.Reset();
        EventSubscription.SetRange("Subscriber Codeunit ID", 70783);
        Assert.AreEqual(
            0, EventSubscription.Count(),
            'Event Subscription must not list a subscriber belonging to a different bundle of the same run.');
    end;

    [Test]
    procedure AllObjDoesNotListTheForeignCodeunitB()
    // The control. AllObj is scoped correctly on the unfixed runner, so this arm passes
    // both before and after the fix — which is exactly what makes the foreign arm above
    // attributable to THIS table rather than to the process model.
    var
        AllObj: Record AllObj;
    begin
        AllObj.Reset();
        AllObj.SetRange("Object Type", AllObj."Object Type"::Codeunit);
        AllObj.SetRange("Object ID", 70783);
        Assert.AreEqual(
            0, AllObj.Count(),
            'AllObj is bundle-scoped on the runner; if this arm fails the defect is the process model, not this table.');
    end;
}
