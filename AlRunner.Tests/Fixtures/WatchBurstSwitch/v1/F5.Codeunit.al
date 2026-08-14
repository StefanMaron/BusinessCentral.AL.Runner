/// <summary>Version A addend #5: always returns 1.</summary>
codeunit 60206 "Burst F5 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(1);
    end;
}
