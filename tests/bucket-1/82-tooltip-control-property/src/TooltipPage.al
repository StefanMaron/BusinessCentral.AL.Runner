table 82000 "TCP Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
        field(4; Active; Boolean) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 82000 "TCP Test Card"
{
    PageType = Card;
    SourceTable = "TCP Test Record";
    Tooltip = 'Page-level tooltip';

    layout
    {
        area(Content)
        {
            group(General)
            {
                field(IdField; Rec.Id)
                {
                    Tooltip = 'The unique identifier';
                }
                field(NameField; Rec.Name)
                {
                    Tooltip = 'The name of the record';
                }
                field(AmountField; Rec.Amount)
                {
                    Tooltip = 'The monetary amount';
                }
                field(ActiveField; Rec.Active)
                {
                    Tooltip = 'Whether the record is active';
                }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(DoSomething)
            {
                Caption = 'Do Something';
                Tooltip = 'Perform the main action';
                trigger OnAction()
                begin
                end;
            }
        }
    }
}
