codeunit 1280001 "CheckType Logic"
{
    /// <summary>
    /// Returns the discount amount when Line Discount % is non-zero.
    /// This triggers CheckType calls in the BC-generated C# code because
    /// comparing a field value (if "Line Discount %" <> 0) emits CheckType.
    /// </summary>
    procedure CalcDiscountAmount(var Rec: Record "CheckType Base Table"): Decimal
    var
        DiscountPct: Decimal;
    begin
        DiscountPct := Rec."Line Discount %";
        if DiscountPct <> 0 then
            exit(Rec."Amount" * DiscountPct / 100);
        exit(0);
    end;

    /// <summary>
    /// Tests that Extra Code comparison (Code field) also works through CheckType.
    /// </summary>
    procedure HasExtraCode(var Rec: Record "CheckType Base Table"): Boolean
    begin
        if Rec."Extra Code" <> '' then
            exit(true);
        exit(false);
    end;
}
