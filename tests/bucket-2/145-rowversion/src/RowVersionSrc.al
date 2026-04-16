/// Helper codeunit exercising Database.LastUsedRowVersion / Database.MinimumActiveRowVersion.
codeunit 59380 "RV Src"
{
    procedure GetLastUsedRowVersion(): BigInteger
    begin
        exit(Database.LastUsedRowVersion);
    end;

    procedure GetMinimumActiveRowVersion(): BigInteger
    begin
        exit(Database.MinimumActiveRowVersion);
    end;

    procedure LastUsedNonNegative(): Boolean
    begin
        exit(Database.LastUsedRowVersion >= 0);
    end;

    procedure MinActiveNonNegative(): Boolean
    begin
        exit(Database.MinimumActiveRowVersion >= 0);
    end;
}
