/// <summary>Version A addend #2: always returns 1.</summary>
codeunit 60203 "Burst F2 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(1);
    end;
}
