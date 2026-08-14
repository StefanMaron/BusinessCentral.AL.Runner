/// <summary>Version B addend #5: always returns 10.</summary>
codeunit 60206 "Burst F5 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}
