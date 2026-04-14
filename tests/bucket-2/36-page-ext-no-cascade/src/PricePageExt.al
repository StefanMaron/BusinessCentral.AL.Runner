pageextension 53600 "Price Page Ext" extends "Price Page"
{
    layout
    {
        addlast(Content)
        {
            field("Custom Markup"; CustomMarkup)
            {
                Editable = PriceIsEditable;
            }
        }
    }

    var
        CustomMarkup: Decimal;
        PriceIsEditable: Boolean;

    trigger OnAfterGetCurrRecord()
    begin
        CustomMarkup := Rec."Unit Price" * 1.1;
        PriceIsEditable := Rec."Unit Price" > 0;
    end;
}
