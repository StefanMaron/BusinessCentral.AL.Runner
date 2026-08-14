/// <summary>Version B addend #0: always returns 10.</summary>
codeunit 60201 "Burst F0 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}
