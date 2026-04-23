// Source codeunit for issue #1105 — AlScope.Parent instance access.
//
// When a codeunit procedure assigns to a codeunit-level variable using
// `this.SomeVar := value;`, the BC compiler generates a scope class that
// accesses the parent codeunit via `this.Parent` (instance access on AlScope).
// With AlScope.Parent defined as static, CS0176 fires: "Member cannot be
// accessed with an instance reference."
//
// The fix: change AlScope.Parent from static to instance property.
codeunit 98301 "Scope Parent Instance Src"
{
    var
        IsActivated: Boolean;
        Counter: Integer;

    procedure Activate()
    begin
        this.IsActivated := true;
    end;

    procedure Deactivate()
    begin
        this.IsActivated := false;
    end;

    procedure Increment(Amount: Integer)
    begin
        this.Counter += Amount;
    end;

    procedure Reset()
    begin
        this.IsActivated := false;
        this.Counter := 0;
    end;

    procedure GetActivated(): Boolean
    begin
        exit(IsActivated);
    end;

    procedure GetCounter(): Integer
    begin
        exit(Counter);
    end;
}
