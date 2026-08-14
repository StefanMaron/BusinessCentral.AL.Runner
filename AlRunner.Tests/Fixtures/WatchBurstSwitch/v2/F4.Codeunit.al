/// <summary>Version B addend #4: always returns 10.</summary>
codeunit 60205 "Burst F4 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}
