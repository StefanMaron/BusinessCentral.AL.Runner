/// <summary>Version B addend #1: always returns 10.</summary>
codeunit 60202 "Burst F1 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}
