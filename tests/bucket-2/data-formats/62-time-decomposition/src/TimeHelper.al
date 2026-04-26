codeunit 62000 "Time Decomposition Helper"
{
    // Returns hour component as 2-char string using <Hours24,2> picture format
    procedure GetHour(T: Time): Text[2]
    begin
        exit(Format(T, 0, '<Hours24,2>'));
    end;

    // Returns minute component as 2-char string using <Minutes,2> picture format
    procedure GetMinute(T: Time): Text[2]
    begin
        exit(Format(T, 0, '<Minutes,2>'));
    end;

    // Returns second component as 2-char string using <Seconds,2> picture format
    procedure GetSecond(T: Time): Text[2]
    begin
        exit(Format(T, 0, '<Seconds,2>'));
    end;

    // Returns HH:MM:SS formatted string
    procedure FormatHMS(T: Time): Text[8]
    begin
        exit(Format(T, 0, '<Hours24,2>:<Minutes,2>:<Seconds,2>'));
    end;

    // Extracts the Time component from a DateTime value
    procedure GetTimeFromDateTime(DT: DateTime): Time
    begin
        exit(DT2Time(DT));
    end;
}
