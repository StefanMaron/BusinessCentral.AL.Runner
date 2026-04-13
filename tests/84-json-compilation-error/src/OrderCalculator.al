/// A simple codeunit that processes order amounts.
/// This file compiles successfully and is used to verify that passing
/// tests still get status "pass" even when other files are excluded by
/// Roslyn (e.g., the XmlPort below).
codeunit 50841 "Order Calculator"
{
    procedure TotalWithTax(Amount: Decimal; TaxRate: Decimal): Decimal
    begin
        exit(Amount * (1 + TaxRate / 100));
    end;
}
