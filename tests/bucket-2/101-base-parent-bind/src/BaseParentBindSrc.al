// Reproducer for base.Parent.Bind() null-dereference — issue #1111.
//
// When an EventSubscriberInstance=Manual codeunit has a procedure that:
//   (a) accesses a codeunit-level variable via `this.Var` (forces a scope class), AND
//   (b) calls BindSubscription(this) in the same procedure body,
// the BC compiler emits base.Parent.Bind() inside the scope class.
//
// The rewriter rewrites ALSession.ALBindSubscription(DataError, target) → target.Bind(),
// but when target is base.Parent (unresolved by the base.Parent→_parent rewrite because
// base.Parent appears as a bare argument, not base.Parent.field), the resulting
// base.Parent.Bind() is never re-visited. AlScope.Parent returns null, so calling
// Bind() on it throws: "Cannot perform runtime binding on a null reference".
//
// Fix: AlScope.Parent must return `this` instead of null, so base.Parent.Bind()
// calls AlScope.Bind() (the no-op stub) rather than dereferencing null.

codeunit 98500 "BPB Subscriber"
{
    EventSubscriberInstance = Manual;

    var
        BindCount: Integer;

    /// Called by tests to bind this subscriber and fire the event in one shot.
    /// The `this.BindCount += 1` forces a scope class, and `BindSubscription(this)`
    /// inside the same procedure emits `base.Parent.Bind()` in that scope class.
    procedure RegisterAndFire(var Publisher: Codeunit "BPB Publisher")
    begin
        this.BindCount += 1;   // forces a scope class → base.Parent.Bind() is emitted here
        BindSubscription(this);
        Publisher.Fire();
        UnbindSubscription(this);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"BPB Publisher", 'OnFire', '', true, true)]
    local procedure HandleFire()
    begin
        BindCount += 1;
    end;

    procedure GetBindCount(): Integer
    begin
        exit(BindCount);
    end;

    procedure Reset()
    begin
        BindCount := 0;
    end;
}

codeunit 98501 "BPB Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnFire()
    begin
    end;

    procedure Fire()
    begin
        OnFire();
    end;
}
