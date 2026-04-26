table 313200 "PRB Demo Tbl"
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

page 313200 "PRB Demo Page"
{
    PageType = Card;
    SourceTable = "PRB Demo Tbl";

    layout
    {
        area(content)
        {
            field(No; "No.") { ApplicationArea = All; }
            field(Amount; Amount) { ApplicationArea = All; }
        }
    }

    procedure Echo(Input: Integer): Integer
    begin
        exit(Input + 1);
    end;

    procedure CountRows(): Integer
    begin
        exit(Rec.Count());
    end;

    procedure SumAmount(): Decimal
    var
        Total: Decimal;
    begin
        Total := 0;
        if Rec.FindSet() then
            repeat
                Total += Rec.Amount;
            until Rec.Next() = 0;
        exit(Total);
    end;
}
