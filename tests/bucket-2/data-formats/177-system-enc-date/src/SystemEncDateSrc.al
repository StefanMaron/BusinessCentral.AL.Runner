codeunit 104000 "SED Src"
{
    procedure IsEncEnabled(): Boolean
    begin
        exit(EncryptionEnabled());
    end;

    procedure EncKeyExists(): Boolean
    begin
        exit(EncryptionKeyExists());
    end;

    procedure DMYToDate(Day: Integer; Month: Integer; Year: Integer): Date
    begin
        exit(DMY2Date(Day, Month, Year));
    end;

    procedure DWYToDate(Day: Integer; Week: Integer; Year: Integer): Date
    begin
        exit(DWY2Date(Day, Week, Year));
    end;

    procedure NDate(D: Date): Date
    begin
        exit(NormalDate(D));
    end;

    procedure CDate(D: Date): Date
    begin
        exit(ClosingDate(D));
    end;

    procedure VarToDate(V: Variant): Date
    begin
        exit(Variant2Date(V));
    end;

    procedure VarToTime(V: Variant): Time
    begin
        exit(Variant2Time(V));
    end;

    procedure DaTiToVar(D: Date; T: Time): Variant
    begin
        exit(DaTi2Variant(D, T));
    end;
}
