table 59870 "VCA Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Price; Decimal) { }
        field(4; IsActive; Boolean) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Page with controls that use Visible = <expression>. BC AL supports two forms:
/// 1. A direct reference to a page-level boolean variable (e.g. `Visible = ShowPrice`)
/// 2. A simple expression — often pre-computed into a boolean via trigger logic
/// The Visible property value is captured at render time; the runner has no UI
/// so the Visible expression never affects execution, but the compilation unit
/// must still compile with these constructs present.
page 59870 "VCA Item Card"
{
    PageType = Card;
    SourceTable = "VCA Item";

    layout
    {
        area(Content)
        {
            field(NoField; Rec."No.") { }
            field(NameField; Rec.Name) { }
            field(PriceField; Rec.Price)
            {
                // Conditional Visible attribute referencing a page-level boolean.
                Visible = ShowPrice;
            }
            field(ActiveField; Rec.IsActive)
            {
                // Another conditional Visible — referencing a negated boolean.
                Visible = not HideActiveFlag;
            }
        }
    }

    var
        ShowPrice: Boolean;
        HideActiveFlag: Boolean;

    trigger OnOpenPage()
    begin
        ShowPrice := Rec.Price > 0;
        HideActiveFlag := false;
    end;
}

/// Helper codeunit — proves the compilation unit containing a page with
/// conditional Visible attributes compiles and codeunits remain callable.
codeunit 59870 "VCA Helper"
{
    procedure GetLabel(): Text
    begin
        exit('visible-conditional');
    end;

    procedure ShouldShowPrice(price: Decimal): Boolean
    begin
        // Mirror the trigger's logic so tests can exercise it without the page.
        exit(price > 0);
    end;

    procedure ShouldShowActive(hideFlag: Boolean): Boolean
    begin
        exit(not hideFlag);
    end;
}
