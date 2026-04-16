/// Helper codeunit exercising CreateDateTime / DT2Date / DT2Time built-ins.
codeunit 60500 "CDT Helper"
{
    procedure MakeDateTime(d: Date; t: Time): DateTime
    begin
        exit(CreateDateTime(d, t));
    end;

    procedure ExtractDate(dt: DateTime): Date
    begin
        exit(DT2Date(dt));
    end;

    procedure ExtractTime(dt: DateTime): Time
    begin
        exit(DT2Time(dt));
    end;

    /// Returns true when CreateDateTime round-trips through DT2Date and DT2Time.
    procedure RoundTrip(d: Date; t: Time): Boolean
    var
        dt: DateTime;
    begin
        dt := CreateDateTime(d, t);
        exit((DT2Date(dt) = d) and (DT2Time(dt) = t));
    end;

    /// Returns true when DT2Date of a 0DT is 0D (the zero DateTime).
    procedure ZeroDateTimeIsZeroDate(): Boolean
    var
        dt: DateTime;
    begin
        exit(DT2Date(dt) = 0D);
    end;
}
