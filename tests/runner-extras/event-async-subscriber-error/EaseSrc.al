/// <summary>
/// Level 1 publisher — stands in for BC's platform trigger codeunit
/// (Codeunit 2000000005 ReportingTriggers), which raises the event the System
/// App subscribes to.
/// </summary>
codeunit 61930 "EASE Level1 Publisher"
{
    [IntegrationEvent(false, false)]
    local procedure OnLevel1(Tag: Text; var Handled: Boolean)
    begin
    end;

    procedure Publish(Tag: Text) Handled: Boolean
    begin
        OnLevel1(Tag, Handled);
    end;
}

/// <summary>
/// Level 2 publisher — stands in for the System App's ReportManagement, which is
/// itself a SUBSCRIBER (to level 1) whose body RAISES ANOTHER EVENT. That second
/// raise is what forces BC to emit this subscriber as an async state machine, and
/// the state machine is what captures any exception raised underneath it.
/// </summary>
codeunit 61931 "EASE Relay"
{
    [IntegrationEvent(false, false)]
    local procedure OnLevel2(Tag: Text; var Handled: Boolean)
    begin
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"EASE Level1 Publisher", 'OnLevel1', '', true, true)]
    local procedure OnLevel1(Tag: Text; var Handled: Boolean)
    begin
        OnLevel2(Tag, Handled);
    end;
}

/// <summary>
/// The ISV subscriber at the bottom of the chain. Raising an error here is the AL
/// author's way of reporting that the work could not be done; it must reach the
/// caller, never be discarded.
/// </summary>
codeunit 61932 "EASE Leaf"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"EASE Relay", 'OnLevel2', '', true, true)]
    local procedure OnLevel2(Tag: Text; var Handled: Boolean)
    begin
        if Tag = 'raise' then
            Error('LEAF-RAISED-THIS');
        Handled := true;
    end;
}
