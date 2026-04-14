codeunit 52801 "Ext Field Logic"
{
    procedure DoubleCustomAmount(var Rec: Record "Base Table"): Decimal
    begin
        exit(Rec."Custom Amount" * 2);
    end;

    procedure SetCustomFields(var Rec: Record "Base Table"; Amount: Decimal; Code: Code[10])
    begin
        Rec."Custom Amount" := Amount;
        Rec."Custom Code" := Code;
    end;
}
