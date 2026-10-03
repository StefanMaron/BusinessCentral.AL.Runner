/// <summary>
/// #5286: codeunits a test runs with Codeunit.Run, whose OnRun reaches a missing member directly.
/// </summary>
codeunit 72304 "Trg Runner"
{
    trigger OnRun()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingRun(1);
    end;
}

/// <summary>
/// #5286: its OnRun raises an event whose subscriber ("Trg Run Subscriber") calls a missing member.
/// </summary>
codeunit 72305 "Trg Event Runner"
{
    trigger OnRun()
    begin
        OnRunEvent();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnRunEvent()
    begin
    end;
}

codeunit 72306 "Trg Run Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Trg Event Runner", 'OnRunEvent', '', false, false)]
    local procedure HandleRunEvent()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingRunEvent(1);
    end;
}

/// <summary>#5286: run with a record argument; its OnRun reaches a missing member.</summary>
codeunit 72307 "Trg Rec Runner"
{
    TableNo = "Trg Quiet Rec";

    trigger OnRun()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingRunRec(1);
    end;
}

/// <summary>#5286, the control: an OnRun that reaches no missing member.</summary>
codeunit 72308 "Trg Quiet Runner"
{
    trigger OnRun()
    begin
    end;
}

/// <summary>
/// #5286, the control: a procedure of its own named Run, which takes a parameter, so a call to it is not
/// Codeunit.Run. Its OnRun reaches a missing member, which the call must not reach.
/// </summary>
codeunit 72309 "Trg Own Run"
{
    trigger OnRun()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingOwnRun(1);
    end;

    procedure Run(Value: Integer): Integer
    begin
        exit(Value);
    end;
}
