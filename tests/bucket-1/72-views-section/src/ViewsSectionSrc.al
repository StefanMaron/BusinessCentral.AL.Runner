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

    views
    {
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
}

/// A pageextension on a list page that adds its own views — exercises both
/// view_definition and the views section inside a pageextension.
pageextension 59200 "VS Product List Ext" extends "VS Product List"
{
    views
    {
        addlast
        {
            view(CategoryA)
            {
                Caption = 'Category A';
                Filters = where(Category = const('A'));
            }
        }
    }
}

/// Business logic helper — proves that the compilation unit containing
/// the pages with views sections compiles and runs correctly.
codeunit 59200 "VS Product Helper"
{
    procedure CountInStock(Stock: Integer): Boolean
    begin
        exit(Stock > 0);
    end;

    procedure CategoryLabel(Category: Code[10]): Text
    begin
        case Category of
            'A':
                exit('Premium');
            'B':
                exit('Standard');
            else
                exit('Other');
        end;
    end;
}
