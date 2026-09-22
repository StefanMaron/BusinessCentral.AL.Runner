// #4447: a page in this app group, so the Page Metadata visibility assertions have a subject.
// An earlier probe asserted "group A does not see a page" in a run where no group declared a
// page at all -- green, and about nothing.
page 62613 "AGV B Page"
{
    PageType = List;
    SourceTable = "AGV B Table";
    ApplicationArea = All;
    UsageCategory = Lists;
    Caption = 'AGV B Page Caption';

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("Code"; Rec."Code") { ApplicationArea = All; }
            }
        }
    }
}
