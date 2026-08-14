/// <summary>Version B addend #3: always returns 10.</summary>
codeunit 60204 "Burst F3 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(10);
    end;
}
