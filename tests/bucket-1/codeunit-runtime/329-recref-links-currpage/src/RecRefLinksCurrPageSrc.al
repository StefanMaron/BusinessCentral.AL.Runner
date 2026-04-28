table 1320420 "RR Link Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { }
    }
}

page 1320421 "RR Child Page"
{
    PageType = ListPart;
    SourceTable = "RR Link Table";

    layout
    {
        area(content)
        {
            repeater(Group)
            {
                field("No."; "No.") { }
            }
        }
    }
}

page 1320422 "RR Parent Page"
{
    PageType = Card;
    SourceTable = "RR Link Table";

    layout
    {
        area(content)
        {
            group(General)
            {
                field("No."; "No.") { }
            }
            part(ChildPart; "RR Child Page")
            {
                SubPageLink = "No." = field("No.");
            }
        }
    }

    procedure ExerciseCurrPageMethods(): Boolean
    var
        Rec: Record "RR Link Table";
    begin
        Rec."No." := 'P1';
        CurrPage.GetRecord(Rec);
        CurrPage.ChildPart.Page.SetRecord(Rec);
        exit(Rec."No." = 'P1');
    end;
}

codeunit 1320414 "RR Links CurrPage Src"
{
    procedure RecordRefHasLinksAfterAdd(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RR Link Table");
        RecRef.AddLink('https://example.com');
        exit(RecRef.HasLinks());
    end;

    procedure RecordRefDeleteLinksClears(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RR Link Table");
        RecRef.AddLink('https://example.com');
        RecRef.DeleteLinks();
        exit(RecRef.HasLinks());
    end;

    procedure PageCurrPageCalls(): Boolean
    var
        PageRef: Page "RR Parent Page";
    begin
        exit(PageRef.ExerciseCurrPageMethods());
    end;
}
