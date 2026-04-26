table 50140 "CNT Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Active; Boolean) { }
        field(4; Category; Code[10]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 50140 "CNT Helper"
{
    procedure InsertItems(n: Integer)
    var
        Rec: Record "CNT Item";
        i: Integer;
    begin
        for i := 1 to n do begin
            Rec.Init();
            Rec."No." := Format(i);
            Rec.Name := 'Item ' + Format(i);
            Rec.Active := (i mod 2 = 1);
            if i mod 3 = 0 then
                Rec.Category := 'C'
            else
                Rec.Category := 'A';
            Rec.Insert();
        end;
    end;

    procedure GetCount(): Integer
    var
        Rec: Record "CNT Item";
    begin
        exit(Rec.Count());
    end;

    procedure GetActiveCount(): Integer
    var
        Rec: Record "CNT Item";
    begin
        Rec.SetRange(Active, true);
        exit(Rec.Count());
    end;

    procedure GetCountByCategory(Cat: Code[10]): Integer
    var
        Rec: Record "CNT Item";
    begin
        Rec.SetRange(Category, Cat);
        exit(Rec.Count());
    end;

    procedure IsEmptyTable(): Boolean
    var
        Rec: Record "CNT Item";
    begin
        exit(Rec.IsEmpty());
    end;
}
