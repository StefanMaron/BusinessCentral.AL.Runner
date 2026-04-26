table 307900 "PAF Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Amount; Decimal)
        {
            AutoFormatType = 1;
        }
        field(3; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 307900 "PAF Test Card"
{
    PageType = Card;
    SourceTable = "PAF Test Record";

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id) { }
            field(AmountField; Rec.Amount)
            {
                AutoFormatType = 1;
            }
            field(NameField; Rec.Name) { }
        }
    }
}

codeunit 307901 "PAF Helper"
{
    procedure InsertAndGetAmount(Id: Integer; Amount: Decimal): Decimal
    var
        Rec: Record "PAF Test Record";
    begin
        Rec.Id := Id;
        Rec.Amount := Amount;
        Rec.Insert(false);
        Rec.Get(Id);
        exit(Rec.Amount);
    end;
}
