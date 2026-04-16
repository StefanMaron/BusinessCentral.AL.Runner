/// Helper codeunit exercising Database.ChangeUserPassword.
/// Signature per the AL compiler: `ChangeUserPassword(OldPassword: Text, NewPassword: Text)`.
codeunit 59590 "CUP Src"
{
    procedure CallChangePassword(OldPassword: Text; NewPassword: Text)
    begin
        Database.ChangeUserPassword(OldPassword, NewPassword);
    end;

    procedure CallChangePasswordAndReturnFlag(OldPassword: Text; NewPassword: Text): Boolean
    begin
        Database.ChangeUserPassword(OldPassword, NewPassword);
        exit(true);
    end;
}
