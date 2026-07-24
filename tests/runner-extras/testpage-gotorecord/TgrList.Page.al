/// <summary>
/// The TestPage under test. A plain list page over "TGR Row" — GoToRecord's job is
/// to move the page's cursor onto the row identified by the record's primary key.
/// </summary>
page 61810 "TGR List"
{
    PageType = List;
    SourceTable = "TGR Row";
    ApplicationArea = All;
    UsageCategory = Lists;

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
                field(Descr; Rec.Descr)
                {
                    ApplicationArea = All;
                }
            }
        }
    }
}
