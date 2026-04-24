page 310003 "PVB Test Page"
{
    PageType = Card;
    SourceTable = "PVB Test Record";

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
        }
    }

    trigger OnAfterGetRecord()
    begin
        FieldCaption := 'Custom Caption';
        TableName := 'Custom Table';
    end;

    var
        FieldCaption: Text;
        TableName: Text;
}
