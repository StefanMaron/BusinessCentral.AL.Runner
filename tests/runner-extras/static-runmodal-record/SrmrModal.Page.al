page 64501 "Srmr Modal"
{
    PageType = StandardDialog;
    SourceTable = "Srmr Row";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            field(Descr; Rec.Descr)
            {
                ApplicationArea = All;
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(OK)
            {
                ApplicationArea = All;
            }
            action(Cancel)
            {
                ApplicationArea = All;
            }
        }
    }
}
