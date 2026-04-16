/// Report whose requestpage trigger exercises CurrPage stub methods.
/// These compile-time uses of CurrPage in requestpage triggers require
/// Caption, Editable, LookupMode, ObjectId, Activate, Update, Close,
/// SaveRecord, SetSelectionFilter to be available on the requestpage class.
report 91000 "RPC Report"
{
    dataset { }

    requestpage
    {
        trigger OnOpenPage()
        var
            cap: Text;
            editable: Boolean;
            lookupMode: Boolean;
            objId: Text[30];
        begin
            CurrPage.Caption := 'Test Caption';
            cap := CurrPage.Caption;

            CurrPage.Editable := true;
            editable := CurrPage.Editable;

            CurrPage.LookupMode := false;
            lookupMode := CurrPage.LookupMode;

            objId := CurrPage.ObjectId(false);

            CurrPage.Activate();
            CurrPage.Update(false);
            CurrPage.SaveRecord();
        end;
    }
}

codeunit 91001 "RPC Helper"
{
    procedure RunReport()
    begin
        Report.Run(91000);
    end;
}
