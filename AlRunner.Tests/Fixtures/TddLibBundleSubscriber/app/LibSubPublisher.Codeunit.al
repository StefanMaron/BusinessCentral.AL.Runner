/// <summary>
/// #5264: the publisher. The subscribers sit in the library and the test bundle, which are compiled
/// after this one. "DoIt" raises the event the library subscribes to; "DoItTwice" reaches it through
/// "DoIt"; "Quiet" raises an event nobody subscribes to. "DoChain" raises an event whose library
/// subscriber calls "RaiseOther", which raises an event the test bundle subscribes to. "DoViaLib" raises
/// an event whose test bundle subscriber calls a library procedure that reaches a missing member. "RaiseY"
/// raises an event whose library subscriber calls a library procedure that raises "OnX", whose library
/// subscriber reaches a member missing on a codeunit of the library itself: two rounds of subscribers.
/// </summary>
codeunit 71940 "Lib Sub Publisher"
{
    procedure Placeholder()
    begin
    end;

    procedure DoIt()
    begin
        OnDo();
    end;

    procedure DoItTwice()
    begin
        DoIt();
    end;

    procedure DoTestSide()
    begin
        OnTestSide();
    end;

    procedure Quiet()
    begin
        OnQuiet();
    end;

    procedure DoChain()
    begin
        OnChain();
    end;

    procedure DoViaLib()
    begin
        OnViaLib();
    end;

    procedure RaiseX()
    begin
        OnX();
    end;

    procedure RaiseY()
    begin
        OnY();
    end;

    procedure RaiseOther()
    begin
        OnOther();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnDo()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnTestSide()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnQuiet()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnChain()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnOther()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnViaLib()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnX()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnY()
    begin
    end;
}
