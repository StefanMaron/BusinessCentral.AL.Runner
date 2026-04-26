page 310006 "PVR Test Page"
{
    PageType = Card;
    SourceTable = "PVR Test Record";

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id) { }
            field(NameField; Rec.Name) { }
            field(ActiveField; Rec.Active) { }
            field(CanDownloadField; CanDownloadResult)
            {
                Caption = 'Can Download';
            }
        }
    }

    trigger OnAfterGetCurrRecord()
    begin
        CanDownloadResult := Rec.CanDownloadResult();
    end;

    var
        CanDownloadResult: Boolean;
}
