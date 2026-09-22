// #4447: a page in this app group, so the Page Metadata visibility assertions have a subject.
// An earlier probe asserted "group A does not see a page" in a run where no group declared a
// page at all -- green, and about nothing.
page 62603 "AGV A Page"
{
    PageType = List;
    SourceTable = "AGV A Table";
    ApplicationArea = All;
    UsageCategory = Lists;
    Caption = 'AGV A Page Caption';

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
