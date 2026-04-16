table 59200 "VS Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Category; Code[10]) { }
        field(3; Active; Boolean) { }
        field(4; Stock; Integer) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// List page with a views section — the most common place for view_definition
/// in BC. Named views pre-filter the list to a subset of records.
/// The views section has no runtime effect in a unit-test context.
page 59200 "VS Product List"
{
    PageType = List;
    SourceTable = "VS Product";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(Code; Rec.Code) { }
                field(Category; Rec.Category) { }
                field(Active; Rec.Active) { }
                field(Stock; Rec.Stock) { }
            }
        }
    }

    views
    {
        view(AllItems)
        {
            Caption = 'All Items';
        }
        view(ActiveOnly)
        {
            Caption = 'Active Products';
            Filters = where(Active = const(true));
        }
        view(InStockOnly)
        {
            Caption = 'In Stock';
            Filters = where(Stock = filter('>0'));
        }
    }
}

/// Business logic helper — proves that the compilation unit containing
/// the page with a views section compiles and runs correctly.
codeunit 59200 "VS Product Helper"
{
    procedure CountInStock(Stock: Integer): Boolean
    begin
        exit(Stock > 0);
    end;

    procedure CategoryLabel(Category: Text): Text
    begin
        if Category = 'A' then
            exit('Premium');
        if Category = 'B' then
            exit('Standard');
        exit('Other');
    end;
}
