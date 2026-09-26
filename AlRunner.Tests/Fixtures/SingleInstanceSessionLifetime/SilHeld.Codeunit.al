codeunit 71922 "SIL Held"
{
    var
        Held: Record "SIL Row";

    procedure Touch()
    begin
        Held.SetRange(Val, 0);
    end;

    procedure CountRows(): Integer
    begin
        exit(Held.Count());
    end;
}
