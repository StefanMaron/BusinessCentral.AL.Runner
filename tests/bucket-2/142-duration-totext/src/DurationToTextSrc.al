/// Helper codeunit exercising Duration.ToText() and Format(Duration).
codeunit 60700 "DTT Helper"
{
    procedure FormatDurationViaToText(d: Duration): Text
    begin
        exit(d.ToText());
    end;

    procedure FormatDurationViaFormat(d: Duration): Text
    begin
        exit(Format(d));
    end;

    procedure OneDayMs(): Duration
    begin
        exit(86400000); // 24 hours in milliseconds
    end;

    procedure OneHourMs(): Duration
    begin
        exit(3600000); // 1 hour in milliseconds
    end;
}
