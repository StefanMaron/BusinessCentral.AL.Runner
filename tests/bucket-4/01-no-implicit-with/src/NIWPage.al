page 410002 "NIW Test Page"
{
    PageType = Card;
    SourceTable = "NIW Test Record";

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id) { }
            field(FieldCaptionField; FieldCaption)
            {
                Caption = 'Field Caption Display';
            }
            field(TableNameField; TableName)
            {
                Caption = 'Table Name Display';
            }
            field(CanDownloadField; CanDownloadResult)
            {
                Caption = 'Can Download';
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        FieldCaption := 'Custom Caption';
        TableName := 'Custom Table';
        CanDownloadResult := Rec.CanDownloadResult();
    end;

    var
        FieldCaption: Text;
        TableName: Text;
        CanDownloadResult: Boolean;
}
