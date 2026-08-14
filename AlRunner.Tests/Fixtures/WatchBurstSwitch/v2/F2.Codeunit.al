/// <summary>Version B addend #2: always returns 10.</summary>
codeunit 60203 "Burst F2 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}
