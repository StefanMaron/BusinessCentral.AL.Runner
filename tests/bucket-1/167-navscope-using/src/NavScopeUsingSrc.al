// Test for NavScope used in using-scope blocks (BC compiler emits
// "using (var δretValParent = new NavScope(this))" for FindSet/Find results).
// This exercises the compilation path that triggered CS1729 (object ctor 1-arg)
// and CS1674 (object not IDisposable) — issues #1085 and #1090.
table 167001 "NSU Payment"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Amount; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 167002 "NSU Source"
{
    /// <summary>
    /// Returns a record from FindSet — this is the exact pattern that caused
    /// CS1729 / CS1674 because the BC compiler wraps record-returning FindSet
    /// calls in a NavScope using block.
    /// </summary>
    procedure SumPayments(): Decimal
    var
        Payment: Record "NSU Payment";
        Total: Decimal;
    begin
        Total := 0;
        if Payment.FindSet() then
            repeat
                Total += Payment.Amount;
            until Payment.Next() = 0;
        exit(Total);
    end;

    procedure InsertPayment(No: Code[20]; Amt: Decimal)
    var
        Payment: Record "NSU Payment";
    begin
        Payment."No." := No;
        Payment.Amount := Amt;
        Payment.Insert();
    end;
}
