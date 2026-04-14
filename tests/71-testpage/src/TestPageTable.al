table 56700 "TP Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 56700 "TP Test Card"
{
    PageType = Card;
    SourceTable = "TP Test Record";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
            field(AmountField; Rec.Amount) { }
        }
    }
}

codeunit 56701 "TP Confirm Logic"
{
    procedure DoSomethingWithConfirm(): Boolean
    begin
        if not Confirm('Are you sure?') then
            exit(false);
        exit(true);
    end;

    procedure ShowMessage()
    begin
        Message('Hello from codeunit');
    end;
}
