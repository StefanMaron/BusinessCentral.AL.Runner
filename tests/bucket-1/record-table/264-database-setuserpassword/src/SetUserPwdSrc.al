/// Helper codeunit exercising Database.SetUserPassword().
/// In standalone mode the call must be a silent no-op —
/// the runner has no service tier and cannot change real passwords.
codeunit 82050 "SUP Src"
{
    procedure ChangePassword(UserId: Guid; NewPwd: Text): Boolean
    begin
        Database.SetUserPassword(UserId, NewPwd);
        exit(true);
    end;
}
