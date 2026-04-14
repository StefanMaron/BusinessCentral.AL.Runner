codeunit 59701 "EP Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnBeforeCalc(var Amount: Integer; var IsHandled: Boolean)
    begin
    end;

    procedure CalcWithEvent(BaseAmount: Integer): Integer
    var
        Result: Integer;
        Handled: Boolean;
    begin
        Result := BaseAmount;
        Handled := false;
        OnBeforeCalc(Result, Handled);
        if Handled then
            exit(Result);
        exit(Result * 2);
    end;
}

codeunit 59702 "EP Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"EP Publisher", 'OnBeforeCalc', '', true, true)]
    local procedure HandleBeforeCalc(var Amount: Integer; var IsHandled: Boolean)
    begin
        Amount := Amount + 100;
        IsHandled := true;
    end;
}
