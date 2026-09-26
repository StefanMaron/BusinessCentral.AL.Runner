// A record three codeunits below the SingleInstance one: the rebind walk has no depth cut-off.
codeunit 71924 "SIL Deep 1"
{
    var
        Next: Codeunit "SIL Deep 2";

    procedure Touch()
    begin
        Next.Touch();
    end;

    procedure CountRows(): Integer
    begin
        exit(Next.CountRows());
    end;
}

codeunit 71925 "SIL Deep 2"
{
    var
        Next: Codeunit "SIL Deep 3";

    procedure Touch()
    begin
        Next.Touch();
    end;

    procedure CountRows(): Integer
    begin
        exit(Next.CountRows());
    end;
}

codeunit 71926 "SIL Deep 3"
{
    var
        Deep: Record "SIL Row";

    procedure Touch()
    begin
        Deep.SetRange(Val, 0);
    end;

    procedure CountRows(): Integer
    begin
        exit(Deep.Count());
    end;
}
