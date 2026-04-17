/// Helper codeunit for JsonArray typed getters: GetBigInteger, GetByte,
/// GetChar, GetDate, GetDateTime, GetDuration, GetOption, GetTime.
/// These BC 21+ single-arg overloads return the typed value directly.
codeunit 114000 "JAEX Src"
{
    // ── GetBigInteger ─────────────────────────────────────────────────────────

    procedure GetBigInteger(Arr: JsonArray; Idx: Integer): BigInteger
    begin
        exit(Arr.GetBigInteger(Idx));
    end;

    // ── GetByte ───────────────────────────────────────────────────────────────

    procedure GetByte(Arr: JsonArray; Idx: Integer): Byte
    begin
        exit(Arr.GetByte(Idx));
    end;

    // ── GetChar ───────────────────────────────────────────────────────────────

    procedure GetChar(Arr: JsonArray; Idx: Integer): Char
    begin
        exit(Arr.GetChar(Idx));
    end;

    // ── GetDate ───────────────────────────────────────────────────────────────

    procedure GetDate(Arr: JsonArray; Idx: Integer): Date
    begin
        exit(Arr.GetDate(Idx));
    end;

    // ── GetDateTime ───────────────────────────────────────────────────────────

    procedure GetDateTime(Arr: JsonArray; Idx: Integer): DateTime
    begin
        exit(Arr.GetDateTime(Idx));
    end;

    // ── GetDuration ───────────────────────────────────────────────────────────

    procedure GetDuration(Arr: JsonArray; Idx: Integer): Duration
    begin
        exit(Arr.GetDuration(Idx));
    end;

    // ── GetOption ─────────────────────────────────────────────────────────────

    procedure GetOption(Arr: JsonArray; Idx: Integer): Integer
    begin
        exit(Arr.GetOption(Idx));
    end;

    // ── GetTime ───────────────────────────────────────────────────────────────

    procedure GetTime(Arr: JsonArray; Idx: Integer): Time
    begin
        exit(Arr.GetTime(Idx));
    end;
}
