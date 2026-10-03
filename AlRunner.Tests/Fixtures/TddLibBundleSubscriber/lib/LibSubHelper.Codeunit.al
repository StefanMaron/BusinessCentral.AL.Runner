/// <summary>
/// #5264: a test library whose subscriber to the app's "OnDo" calls a member the app does not declare.
/// Nothing calls the subscriber: the app's "DoIt" raises the event. "HandleChain" subscribes to an event
/// of the app and raises another one, which the test bundle subscribes to.
/// </summary>
codeunit 71950 "Lib Sub Helper"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnDo', '', false, false)]
    local procedure HandleDo()
    var
        Publisher: Codeunit "Lib Sub Publisher";
        Result: Integer;
    begin
        Result := Publisher.MissingFromLib(1);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnChain', '', false, false)]
    local procedure HandleChain()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.RaiseOther();
    end;

    procedure Calc(): Integer
    var
        Publisher: Codeunit "Lib Sub Publisher";
        Result: Integer;
    begin
        Result := Publisher.MissingViaHelper(1);
        exit(Result);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnX', '', false, false)]
    local procedure HandleX()
    var
        Target: Codeunit "Lib Sub Target";
        Result: Integer;
    begin
        Result := Target.MissingR(1);
    end;

    procedure CallsRaiseX()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.RaiseX();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Lib Sub Publisher", 'OnY', '', false, false)]
    local procedure HandleY()
    begin
        CallsRaiseX();
    end;
}
