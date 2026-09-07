// The control arm: identical source table, sort and layout, declaring NEITHER trigger. What it
// pins is the negative direction -- that the runner does not raise a trigger on a page that has
// none, and still answers the table's own rows.
page 70644 "PRT Plain List"
{
    PageType = List;
    SourceTable = "PRT Row";
    SourceTableView = sorting("No.") order(descending);
    Editable = false;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
            }
        }
    }
}
