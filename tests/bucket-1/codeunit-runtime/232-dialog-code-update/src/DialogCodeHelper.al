table 232000 "DCU Item"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; Description; Text[100]) { DataClassification = ToBeClassified; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 232000 "DCU Helper"
{
    /// <summary>
    /// Uses Dialog.Update with a Code field value.
    /// BC emits NavValue for Code fields; Dialog.ALUpdate has both NavValue and string overloads
    /// causing CS0121 ambiguity when NavCode is passed (NavCode extends NavValue AND converts to string).
    /// </summary>
    procedure ProcessItemsWithDialog(var Rec: Record "DCU Item"): Text
    var
        Dlg: Dialog;
        Result: Text;
    begin
        Dlg.Open('Processing #1##########');
        if Rec.FindSet() then
            repeat
                // Rec."No." is Code[20] — this triggers CS0121 ambiguity in ALUpdate
                Dlg.Update(1, Rec."No.");
                Result += Rec."No." + ' ';
            until Rec.Next() = 0;
        Dlg.Close();
        exit(Result.TrimEnd());
    end;

    /// <summary>
    /// Passes a Code parameter directly to Dialog.Update.
    /// </summary>
    procedure ShowCodeInDialog(ItemNo: Code[20]): Text
    var
        Dlg: Dialog;
    begin
        Dlg.Open('Item #1##########');
        Dlg.Update(1, ItemNo);
        Dlg.Close();
        exit('Done:' + ItemNo);
    end;
}
