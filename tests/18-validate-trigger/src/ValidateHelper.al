codeunit 50118 "Validate Helper"
{
    procedure CreateEntry(EntryNo: Integer; Name: Text[100]; Qty: Integer; Price: Decimal)
    var
        Rec: Record "Validate Demo";
    begin
        Rec.Init();
        Rec."Entry No." := EntryNo;
        Rec."Unit Price" := Price;
        Rec.Validate("Name", Name);
        Rec.Validate("Quantity", Qty);
        Rec.Insert(true);
    end;
}
