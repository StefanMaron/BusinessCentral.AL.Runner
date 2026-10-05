page 70961 "PF Detail Part"
{
    PageType = ListPart;
    SourceTable = "PF Detail";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            repeater(Details)
            {
                field(Info; Rec.Info) { ApplicationArea = All; }
            }
        }
    }

    trigger OnAfterGetRecord()
    var
        Log: Record "PF Log";
    begin
        Log.Insert();
    end;
}
