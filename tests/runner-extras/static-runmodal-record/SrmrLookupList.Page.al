page 64504 "Srmr Lookup List"
{
    // The page "Srmr Lookup Row".LookupPageId names — Page.RunModal(0, Row) must resolve
    // to this page (issue #1918).
    PageType = List;
    SourceTable = "Srmr Lookup Row";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("No."; Rec."No.")
                {
                    ApplicationArea = All;
                }
            }
        }
    }
}
