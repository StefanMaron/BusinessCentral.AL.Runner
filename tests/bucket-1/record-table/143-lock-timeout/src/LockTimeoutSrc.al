/// Helper codeunit exercising Database.LockTimeout / Database.LockTimeoutDuration.
codeunit 59360 "LT Src"
{
    procedure GetLockTimeout(): Boolean
    begin
        exit(Database.LockTimeout);
    end;

    procedure GetLockTimeoutDuration(): Duration
    begin
        exit(Database.LockTimeoutDuration);
    end;

    procedure SetAndGetLockTimeout(Val: Boolean): Boolean
    begin
        Database.LockTimeout(Val);
        exit(Database.LockTimeout);
    end;
}
