codeunit 60500 "Date Decomposition Helper"
{
    procedure GetDay(D: Date): Integer
    begin
        exit(Date2DMY(D, 1));
    end;

    procedure GetMonth(D: Date): Integer
    begin
        exit(Date2DMY(D, 2));
    end;

    procedure GetYear(D: Date): Integer
    begin
        exit(Date2DMY(D, 3));
    end;

    procedure GetDayOfWeek(D: Date): Integer
    begin
        exit(Date2DWY(D, 1));
    end;

    procedure GetWeekNo(D: Date): Integer
    begin
        exit(Date2DWY(D, 2));
    end;
}
