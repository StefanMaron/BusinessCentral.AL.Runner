// Test for AlScope.Bind()/Unbind() stubs — issue #1106, Gap 2.
// BC emits base.Parent.Bind() / base.Parent.Unbind() in scope classes when
// a codeunit calls BindSubscription(self) or UnbindSubscription(self).
// The rewriter converts base.Parent.Bind() → _parent.Bind(), so the
// codeunit class must expose Bind()/Unbind().

table 168001 "SBU Counter"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; HitCount; Integer) { }
    }
    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}

codeunit 168001 "SBU Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnTrigger()
    begin
    end;

    procedure Fire()
    begin
        OnTrigger();
    end;
}

codeunit 168002 "SBU Manual Sub"
{
    EventSubscriberInstance = Manual;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SBU Publisher", 'OnTrigger', '', true, true)]
    local procedure HandleTrigger()
    var
        C: Record "SBU Counter";
    begin
        if not C.Get(1) then begin
            C.PK := 1;
            C.HitCount := 0;
            C.Insert();
        end;
        C.HitCount += 1;
        C.Modify();
    end;
}
