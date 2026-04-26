codeunit 50212 "Date Formatter"
{
    procedure FormatDateISO(InputDate: Date): Text
    begin
        exit(Format(InputDate, 0, '<Year4>-<Month,2>-<Day,2>'));
    end;
}
