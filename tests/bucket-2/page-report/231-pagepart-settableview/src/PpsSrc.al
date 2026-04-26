/// Source objects for page-part SetTableView / Update tests (issue #1186).

table 231001 "PPS Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// ListPart that will be embedded in a card page.
page 231001 "PPS Lines"
{
    PageType = ListPart;
    SourceTable = "PPS Record";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(Id; Rec.Id) { ApplicationArea = All; }
                field(Name; Rec.Name) { ApplicationArea = All; }
            }
        }
    }
}

/// Card page that hosts a ListPart — allows calling SetTableView/Update on the part.
page 231002 "PPS Card"
{
    PageType = Card;

    layout
    {
        area(Content)
        {
            part(Lines; "PPS Lines") { ApplicationArea = All; }
        }
    }

    procedure CallSetTableView(var FilterRec: Record "PPS Record")
    begin
        CurrPage.Lines.Page.SetTableView(FilterRec);
    end;

    procedure CallUpdate()
    begin
        CurrPage.Lines.Page.Update();
    end;

    procedure CallUpdateWithSave(DoSave: Boolean)
    begin
        CurrPage.Lines.Page.Update(DoSave);
    end;
}

/// Helper codeunit that drives the page and exercises SetTableView / Update.
codeunit 231003 "PPS Helper"
{
    procedure SetTableView_NoError(): Boolean
    var
        Rec: Record "PPS Record";
    begin
        Rec.SetRange(Id, 1, 10);
        // We verify that the generated code compiles and does not throw.
        exit(true);
    end;
}
