codeunit 61000 "DateTime Helper"
{
    procedure GetDate(DT: DateTime): Date
    begin
        exit(DT2Date(DT));
    end;

    procedure GetTime(DT: DateTime): Time
    begin
        exit(DT2Time(DT));
    end;

    procedure BuildDateTime(D: Date; T: Time): DateTime
    begin
        exit(CreateDateTime(D, T));
    end;
}
