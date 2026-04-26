/// Source helpers for Dialog Clear() coverage (issue #964).
/// Exercises Clear(dlg) — which the BC compiler lowers to MockDialog.Clear().
codeunit 97706 "Dialog Clear Src"
{
    procedure OpenAndClear()
    var
        dlg: Dialog;
    begin
        dlg.Open('Processing...');
        dlg.Close();
        Clear(dlg);
    end;

    procedure ClearWithoutOpen()
    var
        dlg: Dialog;
    begin
        Clear(dlg);
    end;

    procedure ClearTwice()
    var
        dlg: Dialog;
    begin
        dlg.Open('Step 1');
        dlg.Close();
        Clear(dlg);
        dlg.Open('Step 2');
        dlg.Close();
        Clear(dlg);
    end;
}
