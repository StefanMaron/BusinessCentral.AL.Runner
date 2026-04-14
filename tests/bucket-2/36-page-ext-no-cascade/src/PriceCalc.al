codeunit 53601 "Price Calc"
{
    procedure CalcTotal(var Rec: Record "Price Table"; Qty: Integer): Decimal
    begin
        exit(Rec."Unit Price" * Qty);
    end;
}
