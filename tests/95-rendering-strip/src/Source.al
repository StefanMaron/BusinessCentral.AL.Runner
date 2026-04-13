codeunit 95001 "RS Logic Helper"
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
