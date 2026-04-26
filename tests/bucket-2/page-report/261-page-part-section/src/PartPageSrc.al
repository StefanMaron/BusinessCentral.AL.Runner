table 50263 "Part Page Item"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; Description; Text[100]) { DataClassification = ToBeClassified; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

page 50263 "Part Sub Page"
{
    PageType = ListPart;
    SourceTable = "Part Page Item";

    layout
    {
        area(Content)
        {
            repeater(Items)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Description; Rec.Description) { ApplicationArea = All; }
            }
        }
    }
}

page 50264 "Part Card Page"
{
    PageType = Card;
    SourceTable = "Part Page Item";

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
        }
        area(factboxes)
        {
            part(ItemList; "Part Sub Page")
            {
                ApplicationArea = All;
            }
            systempart(Links; Links)
            {
                ApplicationArea = All;
            }
        }
    }
}

codeunit 50263 "Part Page Helper"
{
    procedure GetLabel(): Text
    begin
        exit('part section ok');
    end;
}
