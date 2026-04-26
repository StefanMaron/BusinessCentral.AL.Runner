/// Helper codeunit exercising DateTime.ToText() and Decimal.ToText() — the
/// methods issue #480 says are not implemented.
codeunit 59390 "TT Src"
{
    procedure FormatDateTime(dt: DateTime): Text
    begin
        exit(dt.ToText());
    end;

    procedure FormatDecimal(d: Decimal): Text
    begin
        exit(d.ToText());
    end;

    procedure MakeKnownDateTime(): DateTime
    begin
        // Anchor a fixed DateTime so string length / content assertions are
        // timezone-independent (the wall-clock date is preserved end-to-end).
        exit(CreateDateTime(DMY2Date(15, 6, 2025), 120000T));
    end;

    procedure FormatKnownDateTime(): Text
    var
        dt: DateTime;
    begin
        dt := MakeKnownDateTime();
        exit(dt.ToText());
    end;

    procedure FormatDecimalZero(): Text
    var
        d: Decimal;
    begin
        d := 0;
        exit(d.ToText());
    end;

    procedure FormatDecimalSpecific(): Text
    var
        d: Decimal;
    begin
        d := 123.45;
        exit(d.ToText());
    end;
}
