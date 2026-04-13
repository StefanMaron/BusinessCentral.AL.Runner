table 93000 "GBS Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 93001 "GBS Lookup Helper"
{
    procedure InsertAndGetSystemId(Id: Integer; Name: Text[100]): Guid
    var
        Rec: Record "GBS Test Record";
    begin
        Rec.Id := Id;
        Rec.Name := Name;
        Rec.Insert(true);
        exit(Rec.SystemId);
    end;

    procedure FindBySystemId(SystemId: Guid): Text[100]
    var
        Rec: Record "GBS Test Record";
    begin
        Rec.GetBySystemId(SystemId);
        exit(Rec.Name);
    end;

    procedure TryFindBySystemId(SystemId: Guid): Boolean
    var
        Rec: Record "GBS Test Record";
    begin
        exit(Rec.GetBySystemId(SystemId));
    end;
}
