/// <summary>Version A addend #4: always returns 1.</summary>
codeunit 60205 "Burst F4 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(1);
    end;
}
