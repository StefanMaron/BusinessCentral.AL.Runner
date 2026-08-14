/// <summary>Version A addend #1: always returns 1.</summary>
codeunit 60202 "Burst F1 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(1);
    end;
}
