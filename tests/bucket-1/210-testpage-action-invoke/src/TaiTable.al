table 114000 "TAI Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; Flag; Boolean) { DataClassification = ToBeClassified; }
        field(3; Counter; Integer) { DataClassification = ToBeClassified; }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

page 114000 "TAI Card"
{
    PageType = Card;
    SourceTable = "TAI Table";

    layout
    {
        area(Content)
        {
            field(NoField; Rec."No.") { }
            field(FlagField; Rec.Flag) { }
            field(CounterField; Rec.Counter) { }
        }
    }

    actions
    {
        area(processing)
        {
            action(SetFlag)
            {
                Caption = 'Set Flag';
                trigger OnAction()
                begin
                    Rec.Flag := true;
                    Rec.Modify();
                end;
            }
            action(IncrementCounter)
            {
                Caption = 'Increment Counter';
                trigger OnAction()
                begin
                    Rec.Counter += 1;
                    Rec.Modify();
                end;
            }
        }
    }
}
