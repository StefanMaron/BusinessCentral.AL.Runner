table 56720 "Modal Test Data"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 56720 "Modal Edit Page"
{
    PageType = Card;
    SourceTable = "Modal Test Data";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
        }
    }
}

codeunit 56720 "Modal Opener"
{
    procedure OpenModalAndGetResult(): Action
    var
        EditPage: Page "Modal Edit Page";
    begin
        exit(EditPage.RunModal());
    end;
}
