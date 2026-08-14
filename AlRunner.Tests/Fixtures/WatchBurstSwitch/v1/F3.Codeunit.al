/// <summary>Version A addend #3: always returns 1.</summary>
codeunit 60204 "Burst F3 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(1);
    end;
}
