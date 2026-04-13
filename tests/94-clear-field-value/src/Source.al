table 94000 "CFV Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 94001 "CFV Clear Helper"
{
    procedure SetAndClearName(var Rec: Record "CFV Test Record"; Value: Text[100])
    begin
        Rec.Name := Value;
        Clear(Rec.Name);
    end;

    procedure SetAndClearAmount(var Rec: Record "CFV Test Record"; Value: Decimal)
    begin
        Rec.Amount := Value;
        Clear(Rec.Amount);
    end;
}
