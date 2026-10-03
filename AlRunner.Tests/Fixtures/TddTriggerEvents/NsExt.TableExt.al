namespace TrgNsExt;

tableextension 72353 "Trg Ns Ext" extends TrgNs."Trg Ns Rec"
{
    trigger OnAfterInsert()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingNsAfterInsert(1);
    end;
}
