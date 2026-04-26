// Renumbered from 85000 to avoid collision in new bucket layout (#1385).
codeunit 1085000 DialogUser
{
    procedure ProcessWithDialog(ItemCount: Integer): Integer
    var
        ProgressDialog: Dialog;
        i: Integer;
        Result: Integer;
    begin
        ProgressDialog.Open('Processing #1##########');
        for i := 1 to ItemCount do begin
            ProgressDialog.Update(1, i);
            Result += i;
        end;
        ProgressDialog.Close();
        exit(Result);
    end;

    procedure ProcessWithDialogText(Caption: Text): Text
    var
        Dlg: Dialog;
    begin
        Dlg.Open(Caption);
        Dlg.Update(1, 'Running');
        Dlg.Close();
        exit('Done: ' + Caption);
    end;
}
