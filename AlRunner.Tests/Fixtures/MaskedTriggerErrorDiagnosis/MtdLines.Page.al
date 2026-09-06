// The linked part whose row-load trigger raises. The host card page refreshes this part as part
// of its OWN row load (LiveNavTestPage.Loaded -> RefreshLinkedParts -> LiveNavTestPart
// .ReloadLinkedRow -> the part's Loaded), so the error below is raised inside the host's open
// and is what LiveNavTestPage.Loaded converts.
//
// The text is deliberately unmistakable and appears nowhere else in this repository, so an
// assertion that finds it cannot be finding something else.
page 70542 "MTD Lines"
{
    PageType = ListPart;
    SourceTable = "MTD Line";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field("Line No."; Rec."Line No.") { ApplicationArea = All; }
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        Error('MTD-BOOM-70542 the part trigger refused this row');
    end;
}
