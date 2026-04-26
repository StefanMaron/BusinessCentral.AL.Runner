/// Source objects for MockPartFormHandle.Close / GetRecord tests (issue #1325).

table 306001 "PFH Record"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; Id; Integer) { DataClassification = ToBeClassified; }
        field(2; Name; Text[50]) { DataClassification = ToBeClassified; }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// ListPart subpage used to test Close and GetRecord on the part form handle.
page 306001 "PFH Sub Page"
{
    PageType = ListPart;
    SourceTable = "PFH Record";

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

/// Card page that hosts the subpage and exposes Close / GetRecord on the part.
page 306002 "PFH Card Page"
{
    PageType = Card;

    layout
    {
        area(Content)
        {
            part(SubPart; "PFH Sub Page") { ApplicationArea = All; }
        }
    }

    /// Calls CurrPage.SubPart.Page.Close() — must compile and not throw.
    procedure CallPartClose()
    begin
        CurrPage.SubPart.Page.Close();
    end;

    /// Calls CurrPage.SubPart.Page.GetRecord(rec) — must compile and not throw.
    procedure CallPartGetRecord(var Rec: Record "PFH Record")
    begin
        CurrPage.SubPart.Page.GetRecord(Rec);
    end;
}
