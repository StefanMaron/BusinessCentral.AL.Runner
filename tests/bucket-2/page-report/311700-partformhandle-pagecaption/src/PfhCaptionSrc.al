/// Source objects for MockPartFormHandle.PageCaption tests (issue #1440).

table 311700 "PFH Caption Record"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; Id; Integer) { DataClassification = ToBeClassified; }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// ListPart subpage used to test PageCaption on the part form handle.
page 311700 "PFH Caption Sub Page"
{
    PageType = ListPart;
    Caption = 'My Sub Page Caption';
    SourceTable = "PFH Caption Record";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(Id; Rec.Id) { ApplicationArea = All; }
            }
        }
    }
}

/// Card page that hosts the subpage and exposes PageCaption get/set on the part.
page 311701 "PFH Caption Card Page"
{
    PageType = Card;

    layout
    {
        area(Content)
        {
            part(SubPart; "PFH Caption Sub Page") { ApplicationArea = All; }
        }
    }

    /// Gets CurrPage.SubPart.Page.Caption — exercises the PageCaption getter.
    procedure GetPartCaption(): Text
    begin
        exit(CurrPage.SubPart.Page.Caption);
    end;

    /// Sets CurrPage.SubPart.Page.Caption := NewCaption — exercises the PageCaption setter.
    procedure SetPartCaption(NewCaption: Text)
    begin
        CurrPage.SubPart.Page.Caption := NewCaption;
    end;

    /// Round-trip: sets Caption then reads it back.
    procedure SetThenGetPartCaption(NewCaption: Text): Text
    begin
        CurrPage.SubPart.Page.Caption := NewCaption;
        exit(CurrPage.SubPart.Page.Caption);
    end;
}
