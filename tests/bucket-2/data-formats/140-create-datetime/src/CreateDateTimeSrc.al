/// Helper codeunit exercising CreateDateTime / DT2Date / DT2Time built-ins.
// Renumbered from 60500 to avoid collision in new bucket layout (#1385).
codeunit 1060500 "CDT Helper"
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

    /// Boxes CreateDateTime result into a Variant, unboxes, returns DateTime.
    /// Exercises the BC lowering path that routes through ALDaTi2Variant.
    procedure VariantMake(d: Date; t: Time): DateTime
    var
        v: Variant;
        dt: DateTime;
    begin
        v := CreateDateTime(d, t);
        dt := v;
        exit(dt);
    end;

    /// Variant-path round-trip: true when the Variant boxed DateTime decomposes
    /// back to the same date and time.
    procedure VariantRoundTrip(d: Date; t: Time): Boolean
    var
        dt: DateTime;
    begin
        dt := VariantMake(d, t);
        exit((DT2Date(dt) = d) and (DT2Time(dt) = t));
    end;
}
