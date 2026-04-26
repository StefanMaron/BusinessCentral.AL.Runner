// Renumbered from 95001 to avoid collision in new bucket layout (#1385).
codeunit 1095001 "RS Logic Helper"
{
    procedure GetRecordName(Id: Integer): Text[100]
    var
        Rec: Record "RS Test Record";
    begin
        Rec.Id := Id;
        Rec.Name := 'Generated';
        Rec.Insert(false);

        Rec.Get(Id);
        exit(Rec.Name);
    end;
}
