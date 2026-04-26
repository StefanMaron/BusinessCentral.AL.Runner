pageextension 53800 "CurrPage Ext" extends "CurrPage Page"
{
    trigger OnAfterGetCurrRecord()
    begin
        CurrPage.Update(false);
    end;
}
