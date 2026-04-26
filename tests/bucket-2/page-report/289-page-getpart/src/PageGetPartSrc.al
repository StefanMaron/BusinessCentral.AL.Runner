table 164000 "PGP Item"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; Value; Integer) { DataClassification = ToBeClassified; }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// <summary>
/// Subpage with a procedure that can be called via CurrPage.SubPart.Page.GetSelectedValue().
/// </summary>
page 164000 "PGP Sub Page"
{
    PageType = ListPart;
    SourceTable = "PGP Item";
    layout
    {
        area(Content)
        {
            repeater(Items)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Value; Rec.Value) { ApplicationArea = All; }
            }
        }
    }
    procedure GetSelectedValue(): Integer
    begin
        exit(Rec.Value);
    end;

    procedure SetValue(NewValue: Integer)
    begin
        Rec.Value := NewValue;
        Rec.Modify(false);
    end;
}

/// <summary>
/// Card page that accesses its subpage via CurrPage.SubItems.Page.
/// The BC compiler lowers this to CurrPage.GetPart(hash).CreateNavFormHandle(scope).Invoke(hash, args).
/// </summary>
page 164001 "PGP Card Page"
{
    PageType = Card;
    SourceTable = "PGP Item";
    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            part(SubItems; "PGP Sub Page")
            {
                ApplicationArea = All;
            }
        }
    }

    /// <summary>Calls a procedure on the subpage via CurrPage.SubItems.Page.</summary>
    procedure CallGetSelectedValue(): Integer
    begin
        exit(CurrPage.SubItems.Page.GetSelectedValue());
    end;
}
