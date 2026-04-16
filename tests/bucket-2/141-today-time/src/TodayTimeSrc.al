/// Helper codeunit exercising the Today and Time built-ins.
codeunit 60600 "TT Helper"
{
    procedure GetToday(): Date
    begin
        exit(Today);
    end;

    procedure GetCurrentTime(): Time
    begin
        exit(Time);
    end;

    /// Returns true if Today is strictly after 2000-01-01.
    procedure IsAfter2000(): Boolean
    begin
        exit(Today > DMY2Date(1, 1, 2000));
    end;

    /// Returns true if Today is strictly before 2100-01-01.
    procedure IsBefore2100(): Boolean
    begin
        exit(Today < DMY2Date(1, 1, 2100));
    end;
}
