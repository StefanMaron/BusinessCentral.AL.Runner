codeunit 53701 "Event Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnBeforeCalc(var Amount: Decimal)
    begin
    end;

    procedure CalcWithEvent(BaseAmount: Decimal): Decimal
    var
        Result: Decimal;
    begin
        Result := BaseAmount;
        OnBeforeCalc(Result);
        exit(Result * 2);
    end;
}
