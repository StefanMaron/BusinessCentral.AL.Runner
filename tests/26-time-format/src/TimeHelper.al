codeunit 50600 TimeHelper
{
    procedure GetMorningTime(): Time
    begin
        exit(060000T);
    end;

    procedure GetNoonTime(): Time
    begin
        exit(120000T);
    end;
}
