// The source-compiled CONTROL subscriber, on the same table and the same event ordinal as the
// precompiled Base Application subscriber this suite is really about.
//
// It exists to keep the two halves of the claim apart. The AL compiler emits this one as a
// `void` method (its body needs no state machine), so it was invoked and completed correctly
// even before the fix. A test that only asserted "some subscriber on User/OnBeforeModifyEvent
// runs" would therefore have passed against the broken runner and proved nothing.
codeunit 65551 "PAS Control Subscriber"
{
    SingleInstance = true;

    var
        Fired: Boolean;
        LastUserName: Code[50];

    procedure Reset()
    begin
        Fired := false;
        LastUserName := '';
    end;

    procedure DidFire(): Boolean
    begin
        exit(Fired);
    end;

    procedure SeenUserName(): Code[50]
    begin
        exit(LastUserName);
    end;

    [EventSubscriber(ObjectType::Table, Database::User, OnBeforeModifyEvent, '', true, true)]
    local procedure ControlOnBeforeModifyUser(var Rec: Record User; var xRec: Record User; RunTrigger: Boolean)
    begin
        Fired := true;
        LastUserName := Rec."User Name";
    end;
}
