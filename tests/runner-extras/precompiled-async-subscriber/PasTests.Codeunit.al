// Issue #2932 — a PRECOMPILED app's table-event [EventSubscriber] must run to completion.
//
// RUNNER-MECHANISM claim. That Base Application codeunit 9002 "Permission Manager" refuses to
// modify a User row with a blank User Name is plain BC behaviour and needs no corpus test; a real
// service tier has never done anything else. What is asserted here is the runner's own dispatch
// of a subscriber the bundle under test did not compile.
//
// The mechanism, measured on BC 28.1: EventSubscriberPatches.BuildSubscription constructs every
// injected NavEventSubscription with memberId 0, because the runner has no BC member-id table to
// draw a real id from. BC's NavEventScope.CallEventSubscriberInternalAsync branches on exactly
// that:
//
//     if (subscriber.MemberId == 0)
//         subscriber.SubscriberMethodInfo.Invoke(subscriberInstance, parameters);  // result DISCARDED
//     else if (subscriberInstance.__IsAsync)
//         await subscriberInstance.InvokeAsync(subscriber.MemberId, parameters);
//
// The AL compiler emits an async ValueTask method whenever a subscriber body needs a state
// machine, and every Base App / System App subscriber on table 2000000120 measured on 28.1 does:
// Codeunit9002.CheckCurrentUserCanModifyUser, Codeunit418.ValidateLicenseTypeOnAfterInsertUser and
// Codeunit153.CheckSuperPermissionsBeforeModifyUser all return ValueTask. Invoking one by
// reflection and dropping the ValueTask starts the body and abandons it at its first suspension,
// and an Error() raised inside is captured by the state machine instead of propagating. BC's own
// per-subscription NoOfCalls counter was bumped every time, so the subscriber genuinely WAS
// dispatched — it simply never got to raise, and the write it existed to refuse went through with
// no complaint.
//
// A subscriber compiled from AL source inside the bundle under test is usually emitted `void`, so
// it was never affected. That is what made the defect read as "precompiled subscribers never
// fire", and it is why the control below is not enough on its own.
codeunit 65552 "PAS Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "PAS Assert";
        Control: Codeunit "PAS Control Subscriber";

    local procedure NewUser(var User: Record User; Name: Code[50])
    begin
        User.Init();
        User."User Security ID" := CreateGuid();
        User."User Name" := Name;
        User.Insert(false);
    end;

    // THE PROOF. Base Application codeunit 9002's CheckCurrentUserCanModifyUser reaches
    // `Rec.TestField("User Name")` on this row: the record is not temporary and its License Type
    // is Full User, so neither of the two early exits above that line applies. Before the fix
    // Modify(true) returned cleanly and this asserterror failed with "An error was expected
    // inside an ASSERTERROR statement".
    [Test]
    procedure PrecompiledAsyncSubscriber_RaisesItsError_OnBeforeModify()
    var
        User: Record User;
    begin
        NewUser(User, '');
        Assert.AreEqual('', User."User Name", 'precondition: the row must have a blank User Name');
        Assert.IsTrue(User."License Type" = User."License Type"::"Full User",
            'precondition: License Type must not be Agent, or codeunit 9002 exits early');
        Assert.IsTrue(not User.IsTemporary(),
            'precondition: the record must not be temporary, or codeunit 9002 exits early');

        asserterror User.Modify(true);

        // The concrete message, not merely "some error": a runner that raised anything else here
        // would not be running the Base Application subscriber.
        Assert.ExpectedError('User Name must have a value', GetLastErrorText());
    end;

    // The same subscriber must let a well-formed row through. Without this, a fix that made every
    // table-event subscriber throw would pass the test above.
    [Test]
    procedure PrecompiledAsyncSubscriber_AllowsAValidRow_OnBeforeModify()
    var
        User: Record User;
    begin
        NewUser(User, 'PAS-VALID-USER');
        Control.Reset();

        User.Modify(true);

        Assert.IsTrue(Control.DidFire(),
            'the source-compiled control subscriber must have been dispatched');
        Assert.AreEqual('PAS-VALID-USER', Control.SeenUserName(),
            'the subscriber must receive the record actually being modified');
    end;

    // The control on its own: a source-compiled subscriber on the same table and ordinal is
    // dispatched and sees the right record. This passed before the fix too — it is here so a
    // future regression can be attributed to the async half rather than to dispatch in general.
    [Test]
    procedure SourceCompiledSubscriber_StillDispatches_OnBeforeModify()
    var
        User: Record User;
    begin
        NewUser(User, 'PAS-CONTROL');
        Control.Reset();
        Assert.IsTrue(not Control.DidFire(), 'precondition: the control must start unfired');

        User.Modify(true);

        Assert.IsTrue(Control.DidFire(), 'the control subscriber must fire');
        Assert.AreEqual('PAS-CONTROL', Control.SeenUserName(), 'the control must see the modified row');
    end;
}
